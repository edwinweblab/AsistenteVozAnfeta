using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Anfeta.UI.Services.Notion
{
    /// <summary>
    /// Coordina todas las solicitudes a la API de Notion dentro de ANFETA.
    ///
    /// Notion permite un promedio aproximado de tres solicitudes por segundo.
    /// Para dejar margen, ANFETA inicia como máximo una solicitud cada 350 ms,
    /// conserva una sola solicitud activa y respeta Retry-After ante HTTP 429.
    /// </summary>
    public static class NotionRequestCoordinator
    {
        private static readonly SemaphoreSlim RequestGate = new(1, 1);
        private static readonly SemaphoreSlim FullSyncGate = new(1, 1);
        private static readonly object StateLock = new();

        private static readonly TimeSpan MinimumRequestSpacing =
            TimeSpan.FromMilliseconds(350);

        private static DateTimeOffset _nextAllowedRequestUtc =
            DateTimeOffset.MinValue;

        private static DateTimeOffset _cooldownUntilUtc =
            DateTimeOffset.MinValue;

        public static bool IsCoolingDown
        {
            get
            {
                lock (StateLock)
                {
                    return _cooldownUntilUtc > DateTimeOffset.UtcNow;
                }
            }
        }

        public static TimeSpan CooldownRemaining
        {
            get
            {
                lock (StateLock)
                {
                    var remaining =
                        _cooldownUntilUtc - DateTimeOffset.UtcNow;

                    return remaining > TimeSpan.Zero
                        ? remaining
                        : TimeSpan.Zero;
                }
            }
        }


        public static async Task<IDisposable> EnterFullSyncAsync(
            CancellationToken cancellationToken = default)
        {
            await FullSyncGate.WaitAsync(cancellationToken);
            return new SemaphoreLease(FullSyncGate);
        }

        private sealed class SemaphoreLease : IDisposable
        {
            private SemaphoreSlim? _semaphore;

            public SemaphoreLease(SemaphoreSlim semaphore)
            {
                _semaphore = semaphore;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _semaphore, null)?.Release();
            }
        }

        public static async Task<HttpResponseMessage> SendAsync(
            HttpClient http,
            Func<HttpRequestMessage> requestFactory,
            CancellationToken cancellationToken = default,
            int maxAttempts = 5)
        {
            if (http == null)
                throw new ArgumentNullException(nameof(http));

            if (requestFactory == null)
                throw new ArgumentNullException(nameof(requestFactory));

            maxAttempts = Math.Clamp(maxAttempts, 1, 8);
            Exception? lastException = null;

            for (var attempt = 1;
                 attempt <= maxAttempts;
                 attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await RequestGate.WaitAsync(cancellationToken);

                try
                {
                    await WaitUntilAllowedAsync(cancellationToken);

                    using var request = requestFactory();

                    var response =
                        await http.SendAsync(
                            request,
                            HttpCompletionOption.ResponseContentRead,
                            cancellationToken);

                    RegisterRequestFinished();

                    if (!ShouldRetry(response.StatusCode))
                        return response;

                    var delay =
                        GetRetryDelay(response, attempt);

                    RegisterCooldown(delay);

                    if (attempt == maxAttempts)
                        return response;

                    response.Dispose();
                }
                catch (OperationCanceledException ex)
                    when (!cancellationToken.IsCancellationRequested &&
                          attempt < maxAttempts)
                {
                    lastException = ex;
                    RegisterCooldown(GetExponentialDelay(attempt));
                }
                catch (HttpRequestException ex)
                    when (attempt < maxAttempts)
                {
                    lastException = ex;
                    RegisterCooldown(GetExponentialDelay(attempt));
                }
                finally
                {
                    RequestGate.Release();
                }
            }

            throw new HttpRequestException(
                "Notion no respondió después de varios intentos.",
                lastException);
        }

        private static async Task WaitUntilAllowedAsync(
            CancellationToken cancellationToken)
        {
            while (true)
            {
                DateTimeOffset targetUtc;

                lock (StateLock)
                {
                    targetUtc =
                        _nextAllowedRequestUtc > _cooldownUntilUtc
                            ? _nextAllowedRequestUtc
                            : _cooldownUntilUtc;
                }

                var delay = targetUtc - DateTimeOffset.UtcNow;

                if (delay <= TimeSpan.Zero)
                    return;

                await Task.Delay(delay, cancellationToken);
            }
        }

        private static void RegisterRequestFinished()
        {
            lock (StateLock)
            {
                var next =
                    DateTimeOffset.UtcNow + MinimumRequestSpacing;

                if (next > _nextAllowedRequestUtc)
                    _nextAllowedRequestUtc = next;
            }
        }

        private static void RegisterCooldown(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

            // Pequeño margen para no volver a solicitar exactamente en el
            // instante señalado por Retry-After.
            delay += TimeSpan.FromMilliseconds(
                Random.Shared.Next(120, 280));

            lock (StateLock)
            {
                var target = DateTimeOffset.UtcNow + delay;

                if (target > _cooldownUntilUtc)
                    _cooldownUntilUtc = target;
            }
        }

        private static bool ShouldRetry(HttpStatusCode statusCode)
        {
            var numeric = (int)statusCode;

            return statusCode == HttpStatusCode.TooManyRequests ||
                   statusCode == HttpStatusCode.RequestTimeout ||
                   numeric == 529 ||
                   numeric >= 500;
        }

        private static TimeSpan GetRetryDelay(
            HttpResponseMessage response,
            int attempt)
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta &&
                delta > TimeSpan.Zero)
            {
                return delta;
            }

            if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
            {
                var wait = date - DateTimeOffset.UtcNow;

                if (wait > TimeSpan.Zero)
                    return wait;
            }

            if (response.Headers.TryGetValues(
                    "Retry-After",
                    out var values))
            {
                var raw = values.FirstOrDefault();

                if (double.TryParse(
                        raw,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var seconds) &&
                    seconds > 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }

            return GetExponentialDelay(attempt);
        }

        private static TimeSpan GetExponentialDelay(int attempt)
        {
            var seconds =
                Math.Min(30, Math.Pow(2, attempt - 1));

            return TimeSpan.FromSeconds(seconds);
        }
    }
}
