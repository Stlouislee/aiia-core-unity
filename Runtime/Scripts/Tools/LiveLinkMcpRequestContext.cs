using System;
using System.Threading;

namespace LiveLink.Tools
{
    internal static class LiveLinkMcpRequestContext
    {
        private static readonly AsyncLocal<LiveLinkToolConsumer?> CurrentConsumerSlot = new AsyncLocal<LiveLinkToolConsumer?>();

        public static LiveLinkToolConsumer CurrentConsumer
        {
            get
            {
                LiveLinkToolConsumer? value = CurrentConsumerSlot.Value;
                return value.HasValue ? value.Value : LiveLinkToolConsumer.External;
            }
        }

        public static IDisposable PushConsumer(LiveLinkToolConsumer consumer)
        {
            LiveLinkToolConsumer? previous = CurrentConsumerSlot.Value;
            CurrentConsumerSlot.Value = consumer;
            return new PopScope(previous);
        }

        private sealed class PopScope : IDisposable
        {
            private readonly LiveLinkToolConsumer? _previous;
            private bool _disposed;

            public PopScope(LiveLinkToolConsumer? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                CurrentConsumerSlot.Value = _previous;
            }
        }
    }
}
