#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Phoenix
{
    internal static class TaskUtilities
    {
        internal static async Task<T> AwaitAndDisposeCancellationRegistrationAsync<T>(
            Task<T> task,
            CancellationTokenRegistration cancellationRegistration
        )
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                cancellationRegistration.Dispose();
            }
        }
    }
}
