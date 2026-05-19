namespace UpdaterLib
{
    /// <summary>Explicit success/failure for upgrade pipeline steps (download, extract, cleanup).</summary>
    public sealed class UpgradeStepResult
    {
        private UpgradeStepResult(
            bool success,
            string? packagePath,
            string? intermediateArtifactPath,
            string? errorMessage)
        {
            Success = success;
            PackagePath = packagePath;
            IntermediateArtifactPath = intermediateArtifactPath;
            ErrorMessage = errorMessage;
        }

        public bool Success { get; }

        /// <summary>Downloaded .tar.gz path carried through the pipeline.</summary>
        public string? PackagePath { get; }

        /// <summary>Optional decompressed .tar to delete during cleanup (legacy two-step extract).</summary>
        public string? IntermediateArtifactPath { get; }

        public string? ErrorMessage { get; }

        public static UpgradeStepResult Succeeded(
            string packagePath,
            string? intermediateArtifactPath = null) =>
            new(true, packagePath, intermediateArtifactPath, null);

        public static UpgradeStepResult Failed(string errorMessage) =>
            new(false, null, null, errorMessage);
    }
}
