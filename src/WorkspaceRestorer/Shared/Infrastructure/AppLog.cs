using System;
using System.IO;
using System.Text;

namespace WorkspaceRestorer.Infrastructure
{
    internal static class AppLog
    {
        private static readonly object SyncRoot = new object();

        public static event EventHandler<LogEventArgs> MessageWritten;

        public static void Write(string message)
        {
            Write(message, null);
        }

        public static void Write(string message, Exception exception)
        {
            string rendered = WorkspaceRestorer.Core.TimeText.FormatSortableSecond(DateTime.Now)
                + "  " + (message ?? string.Empty);
            if (exception != null)
            {
                rendered += Environment.NewLine + exception;
            }

            lock (SyncRoot)
            {
                try
                {
                    AppPaths.EnsureDataDirectories();
                    string file = Path.Combine(
                        AppPaths.LogDirectory,
                        WorkspaceRestorer.Core.TimeText.FormatDateStamp(DateTime.Now) + ".log");
                    File.AppendAllText(file, rendered + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                }
            }

            EventHandler<LogEventArgs> handler = MessageWritten;
            if (handler != null)
            {
                handler(null, new LogEventArgs(rendered));
            }
        }
    }

    internal sealed class LogEventArgs : EventArgs
    {
        public LogEventArgs(string message)
        {
            Message = message;
        }

        public string Message { get; private set; }
    }
}
