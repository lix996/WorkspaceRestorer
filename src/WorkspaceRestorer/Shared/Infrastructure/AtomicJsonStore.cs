using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace WorkspaceRestorer.Infrastructure
{
    internal sealed class AtomicJsonStore<T> where T : class
    {
        private readonly object syncRoot = new object();
        private readonly string path;
        private readonly Func<T> defaultFactory;
        private readonly DataContractJsonSerializer serializer;

        public AtomicJsonStore(string path, Func<T> defaultFactory)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A storage path is required.", "path");
            }

            this.path = path;
            if (defaultFactory == null)
            {
                throw new ArgumentNullException("defaultFactory");
            }

            this.defaultFactory = defaultFactory;
            this.serializer = new DataContractJsonSerializer(typeof(T));
        }

        public T Load()
        {
            lock (syncRoot)
            {
                if (!File.Exists(path))
                {
                    return defaultFactory();
                }

                try
                {
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        T value = serializer.ReadObject(stream) as T;
                        return value ?? defaultFactory();
                    }
                }
                catch (Exception exception)
                {
                    AppLog.Write("Unable to read " + Path.GetFileName(path) + "; defaults are being used.", exception);
                    return defaultFactory();
                }
            }
        }

        public void Save(T value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }

            lock (syncRoot)
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temporaryPath = path + ".tmp." + System.Diagnostics.Process.GetCurrentProcess().Id;
                try
                {
                    using (FileStream stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        serializer.WriteObject(stream, value);
                        stream.Flush(true);
                    }

                    ReplaceFile(temporaryPath);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void ReplaceFile(string temporaryPath)
        {
            if (!File.Exists(path))
            {
                File.Move(temporaryPath, path);
                return;
            }

            string backupPath = path + ".bak";
            try
            {
                File.Replace(temporaryPath, path, backupPath, true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(temporaryPath, path, true);
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                File.Copy(temporaryPath, path, true);
                File.Delete(temporaryPath);
            }
        }
    }

}
