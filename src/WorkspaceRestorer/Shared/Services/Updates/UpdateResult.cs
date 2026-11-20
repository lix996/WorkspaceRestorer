using System;

namespace WorkspaceRestorer.Services.Updates
{
    internal enum UpdateCheckOutcome
    {
        NotConfigured,
        UpToDate,
        UpdateAvailable,
        Failed,
        Suppressed
    }

    /// <summary>一次检查更新的结果；只做数据，不弹框、不打开浏览器。</summary>
    internal sealed class UpdateResult
    {
        public UpdateCheckOutcome Outcome { get; private set; }
        public string CurrentVersion { get; private set; }
        public string LatestVersion { get; private set; }
        public string ReleaseUrl { get; private set; }
        public string ReleaseNotes { get; private set; }
        public string Error { get; private set; }

        private UpdateResult(UpdateCheckOutcome outcome)
        {
            Outcome = outcome;
            CurrentVersion = string.Empty;
            LatestVersion = string.Empty;
            ReleaseUrl = string.Empty;
            ReleaseNotes = string.Empty;
            Error = string.Empty;
        }

        internal static UpdateResult NotConfigured(string currentVersion)
        {
            UpdateResult result = new UpdateResult(UpdateCheckOutcome.NotConfigured);
            result.CurrentVersion = currentVersion ?? string.Empty;
            return result;
        }

        internal static UpdateResult Suppressed(string currentVersion)
        {
            UpdateResult result = new UpdateResult(UpdateCheckOutcome.Suppressed);
            result.CurrentVersion = currentVersion ?? string.Empty;
            return result;
        }

        internal static UpdateResult UpToDate(string currentVersion, string latestVersion)
        {
            UpdateResult result = new UpdateResult(UpdateCheckOutcome.UpToDate);
            result.CurrentVersion = currentVersion ?? string.Empty;
            result.LatestVersion = latestVersion ?? string.Empty;
            return result;
        }

        internal static UpdateResult UpdateAvailable(
            string currentVersion,
            string latestVersion,
            string releaseUrl,
            string releaseNotes)
        {
            UpdateResult result = new UpdateResult(UpdateCheckOutcome.UpdateAvailable);
            result.CurrentVersion = currentVersion ?? string.Empty;
            result.LatestVersion = latestVersion ?? string.Empty;
            result.ReleaseUrl = releaseUrl ?? string.Empty;
            result.ReleaseNotes = releaseNotes ?? string.Empty;
            return result;
        }

        internal static UpdateResult Failed(string currentVersion, string error)
        {
            UpdateResult result = new UpdateResult(UpdateCheckOutcome.Failed);
            result.CurrentVersion = currentVersion ?? string.Empty;
            result.Error = error ?? string.Empty;
            return result;
        }
    }
}
