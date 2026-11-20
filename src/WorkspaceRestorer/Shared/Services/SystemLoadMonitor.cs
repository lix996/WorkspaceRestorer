using System;
using System.Threading;
using System.Threading.Tasks;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    public sealed class SystemLoadMonitor : ISystemLoadMonitor
    {
        public async Task<SystemLoadSnapshot> ReadAsync(
            TimeSpan sampleInterval,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sampleInterval < TimeSpan.Zero)
            {
                sampleInterval = TimeSpan.Zero;
            }

            NativeMethods.NativeFileTime idleBefore;
            NativeMethods.NativeFileTime kernelBefore;
            NativeMethods.NativeFileTime userBefore;
            bool cpuBeforeAvailable = TryGetSystemTimes(
                out idleBefore,
                out kernelBefore,
                out userBefore);

            if (sampleInterval > TimeSpan.Zero)
            {
                await Task.Delay(sampleInterval, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            NativeMethods.NativeFileTime idleAfter;
            NativeMethods.NativeFileTime kernelAfter;
            NativeMethods.NativeFileTime userAfter;
            bool cpuAfterAvailable = TryGetSystemTimes(
                out idleAfter,
                out kernelAfter,
                out userAfter);

            NativeMethods.MemoryStatusEx memory = NativeMethods.MemoryStatusEx.Create();
            bool memoryAvailable = TryGetMemoryStatus(ref memory);

            SystemLoadSnapshot snapshot = new SystemLoadSnapshot();
            snapshot.CpuPercent = 0;
            snapshot.MemoryPercent = memoryAvailable ? memory.MemoryLoad : 0;
            snapshot.IsAvailable = cpuBeforeAvailable && cpuAfterAvailable && memoryAvailable;

            if (cpuBeforeAvailable && cpuAfterAvailable)
            {
                snapshot.CpuPercent = CalculateCpuPercent(
                    idleBefore,
                    kernelBefore,
                    userBefore,
                    idleAfter,
                    kernelAfter,
                    userAfter);
            }

            if (!snapshot.IsAvailable)
            {
                snapshot.Error = BuildError(cpuBeforeAvailable, cpuAfterAvailable, memoryAvailable);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return snapshot;
        }

        private static bool TryGetSystemTimes(
            out NativeMethods.NativeFileTime idleTime,
            out NativeMethods.NativeFileTime kernelTime,
            out NativeMethods.NativeFileTime userTime)
        {
            idleTime = new NativeMethods.NativeFileTime();
            kernelTime = new NativeMethods.NativeFileTime();
            userTime = new NativeMethods.NativeFileTime();
            try
            {
                return NativeMethods.GetSystemTimes(out idleTime, out kernelTime, out userTime);
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryGetMemoryStatus(ref NativeMethods.MemoryStatusEx memory)
        {
            try
            {
                return NativeMethods.GlobalMemoryStatusEx(ref memory);
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static double CalculateCpuPercent(
            NativeMethods.NativeFileTime idleBefore,
            NativeMethods.NativeFileTime kernelBefore,
            NativeMethods.NativeFileTime userBefore,
            NativeMethods.NativeFileTime idleAfter,
            NativeMethods.NativeFileTime kernelAfter,
            NativeMethods.NativeFileTime userAfter)
        {
            ulong idleDelta = Difference(idleAfter.ToUInt64(), idleBefore.ToUInt64());
            ulong kernelDelta = Difference(kernelAfter.ToUInt64(), kernelBefore.ToUInt64());
            ulong userDelta = Difference(userAfter.ToUInt64(), userBefore.ToUInt64());
            ulong totalDelta = kernelDelta + userDelta;
            if (totalDelta == 0)
            {
                return 0;
            }

            double busyDelta = totalDelta > idleDelta ? totalDelta - idleDelta : 0;
            double percent = busyDelta * 100.0 / totalDelta;
            if (percent < 0)
            {
                return 0;
            }

            return percent > 100 ? 100 : percent;
        }

        private static ulong Difference(ulong newer, ulong older)
        {
            return newer >= older ? newer - older : 0;
        }

        private static string BuildError(bool cpuBeforeAvailable, bool cpuAfterAvailable, bool memoryAvailable)
        {
            if (!cpuBeforeAvailable || !cpuAfterAvailable)
            {
                if (!memoryAvailable)
                {
                    return "Windows did not provide CPU or memory load information.";
                }

                return "Windows did not provide CPU load information.";
            }

            return "Windows did not provide memory load information.";
        }
    }
}
