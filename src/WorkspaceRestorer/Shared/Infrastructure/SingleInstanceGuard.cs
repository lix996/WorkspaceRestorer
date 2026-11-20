using System;
using System.Threading;

namespace WorkspaceRestorer.Infrastructure
{
    internal sealed class SingleInstanceGuard : IDisposable
    {
        private readonly Mutex mutex;
        private bool ownsMutex;

        public SingleInstanceGuard(string name)
        {
            bool createdNew;
            mutex = new Mutex(true, "Local\\" + name, out createdNew);
            ownsMutex = createdNew;
        }

        public bool IsPrimaryInstance { get { return ownsMutex; } }

        public void Dispose()
        {
            if (ownsMutex)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
                ownsMutex = false;
            }
            mutex.Dispose();
        }
    }
}
