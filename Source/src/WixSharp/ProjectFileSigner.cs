using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WixSharp.Utilities;

using IO = System.IO;

namespace WixSharp
{
    /// <summary>
    /// Options for configuring the behavior of the file signing process.
    /// </summary>
    public class SignAllFilesOptions
    {
        /// <summary>
        /// Gets or sets the supported file formats for signing.
        /// </summary>
        public string[] SupportedFileFormats { get; set; } =
        {
            ".dll", ".exe", ".cab", ".msi"
        };

        /// <summary>
        /// Determines whether to skip signing already signed files.
        /// </summary>
        public bool SkipSignedFiles { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether sign WixSharp own embedded assemblies. IE, WixSharp.UI.dll.
        /// <p>The native package with the embedded assemblies (<c>*.CA.dll</c>) is signed as well.</p>
        /// <p>Note, signing embedded assemblies have some logical constrains. IE you assembly being executed cannot be
        /// signed as it is locked by its own process. Thus you should either skip signing such assemblies form the
        /// <see cref="IDigitalSignature.Apply(string)"/> method, or sign the locked assemblies before you build the msi file.
        /// This will work because WixSharp detect already signed assemblies and gracefully skip them.</p>
        /// </summary>
        /// <value>
        ///   <c>true</c> if should sign the embedded assemblies; otherwise, <c>false</c>.
        /// </value>
        public bool SignEmbeddedAssemblies { get; set; } = false;

        /// <summary>
        /// Determines the behavior when an exception occurs during the signing of a specific file.
        /// <para>For example, <see cref="ExceptionHandler"/> can be used to skip and continue the process in DEBUG mode
        /// when it is not critical that all files are signed successfully.</para>
        /// <para>If <see cref="AutoGenerationOptions.FailOnSigningError"/> is enabled, a non-zero exit code of the signing
        /// tool is reported as <see cref="InvalidOperationException"/>. The default handler rethrows the exception and fails the build.</para>
        /// </summary>
        public Action<string, Exception> ExceptionHandler { get; set; } = (file, ex) => throw ex;
    }

    /// <summary>
    /// Provides functionality to sign all files in a project.
    /// </summary>
    static class ProjectFileSigner
    {
        /// <summary>
        /// Signs all files in the specified project that match the given options.
        /// </summary>
        /// <param name="project">The project whose files are to be signed.</param>
        /// <param name="options">The options to configure the signing process.</param>
        public static void SignAllFiles(Project project, SignAllFilesOptions options)
        {
            if (project.DigitalSignature == null)
                return;

            // Resolve wildcards
            project.ResolveWildCards(Compiler.AutoGeneration.IgnoreWildCardEmptyDirectories);

            var actualFilesToSign = new List<string>();

            // Sign all files
            foreach (var file in project.AllFiles)
            {
                try
                {
                    if (file.Name.IsNullOrEmpty())
                        continue;

                    var filePath = Utils.PathCombine(project.SourceBaseDir, file.Name);

                    if (!IO.File.Exists(filePath))
                        continue;

                    // Skip if file is not supported
                    var fileExt = Path.GetExtension(filePath).ToLower();
                    var fileFormat = fileExt.TrimStart('.');

                    if (!options.SupportedFileFormats.Any(x => x.IsOneOf(fileExt, fileFormat)))
                        continue;

                    // Skip if file is already signed
                    if (options.SkipSignedFiles && VerifyFileSignature.IsSigned(filePath))
                        continue;

                    if (filePath.IsFileLocked())
                    {
                        var isolatedFile = Utils.IsolateFile(filePath);

                        var newName = isolatedFile;
                        if (!file.Name.IsAbsolutePath())
                            newName = isolatedFile.MakeRelativeTo(project.SourceBaseDir.IsNotEmpty() ? project.SourceBaseDir : Environment.CurrentDirectory);

                        Compiler.OutputWriteLine($"File {file.Name} is locked.");
                        file.MapTo(newName);

                        actualFilesToSign.Add(isolatedFile);
                    }
                    else
                        actualFilesToSign.Add(filePath);
                }
                catch (Exception ex)
                {
                    options.ExceptionHandler?.Invoke(file.Name, ex);
                }
            }

            SignFiles(project.DigitalSignature, actualFilesToSign, options);
        }

        /// <summary>
        /// Signs the files with the signature, as a batch if <see cref="AutoGenerationOptions.PreferBatchSigning"/> is enabled
        /// and the signature is an <see cref="ISigningTool"/>, otherwise one by one.
        /// <para>A non-zero exit code of the signing tool is handled according to <see cref="AutoGenerationOptions.FailOnSigningError"/>.</para>
        /// </summary>
        /// <param name="signature">The signature to apply.</param>
        /// <param name="files">The files to sign.</param>
        /// <param name="options">The signing options.</param>
        internal static void SignFiles(IDigitalSignature signature, IList<string> files, SignAllFilesOptions options)
        {
            if (signature == null || !files.Any())
                return;

            if (signature is ISigningTool && WixSharp.Compiler.AutoGeneration.PreferBatchSigning)
            {
                var retValue = (signature as ISigningTool).Sign(files.ToArray());
                CheckSigningResult(files.Count == 1 ? files[0] : $"{files.Count} files", retValue, options);
            }
            else
            {
                foreach (var file in files)
                    CheckSigningResult(file, signature.Apply(file), options);
            }
        }

        /// <summary>
        /// Checks the exit code of the signing tool. A non-zero exit code is reported as a warning, or as
        /// <see cref="InvalidOperationException"/> if <see cref="AutoGenerationOptions.FailOnSigningError"/> is enabled.
        /// </summary>
        /// <param name="file">The file (or the description of the file set) that was signed.</param>
        /// <param name="retValue">The exit code of the signing tool.</param>
        /// <param name="options">The signing options with the <see cref="SignAllFilesOptions.ExceptionHandler"/> receiving
        /// the exception. If <c>null</c> (signing the build output, e.g. the msi file), the exception is thrown.</param>
        internal static void CheckSigningResult(string file, int retValue, SignAllFilesOptions options = null)
        {
            if (retValue == 0)
                return;

            if (!WixSharp.Compiler.AutoGeneration.FailOnSigningError)
            {
                Compiler.OutputWriteLine($"WARNING: Signing {file} failed. Return code: {retValue}");
                return;
            }

            var error = new InvalidOperationException($"Signing the file '{file}' failed. Return code: {retValue}");

            if (options == null)
                throw error;

            options.ExceptionHandler?.Invoke(file, error);
        }
    }
}