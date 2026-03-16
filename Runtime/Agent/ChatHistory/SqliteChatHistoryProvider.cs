using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using UnityEngine;

namespace LiveLink.Agent.ChatHistory
{
    /// <summary>
    /// Production-ready SQLite-backed chat history provider for Microsoft Agent Framework.
    /// Supports connection pooling, async I/O, history reduction, and session management.
    /// </summary>
    public sealed class SqliteChatHistoryProvider : ChatHistoryProvider, IDisposable
    {
        private readonly ProviderSessionState<SessionState> _sessionState;
        private readonly SqliteChatHistoryStore _store;
        private readonly SqliteChatHistoryProviderOptions _options;
        private bool _disposed;

        /// <summary>
        /// Gets the state key used to store provider-specific state in the session.
        /// </summary>
        public override string StateKey => _sessionState.StateKey;

        /// <summary>
        /// Gets the underlying store for direct access to conversation management APIs.
        /// </summary>
        public SqliteChatHistoryStore Store => _store;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqliteChatHistoryProvider"/> class.
        /// </summary>
        /// <param name="options">Configuration options for the provider.</param>
        public SqliteChatHistoryProvider(SqliteChatHistoryProviderOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            
            if (string.IsNullOrWhiteSpace(options.DatabasePath))
            {
                throw new ArgumentException("DatabasePath must be specified.", nameof(options));
            }

            _sessionState = new ProviderSessionState<SessionState>(
                stateInitializer: session => new SessionState
                {
                    ConversationId = Guid.NewGuid().ToString("N"),
                    CreatedAt = DateTime.UtcNow
                },
                stateKey: this.GetType().Name);

            _store = new SqliteChatHistoryStore(options);
        }

        /// <summary>
        /// Initializes a new instance with default options using the specified database path.
        /// </summary>
        /// <param name="databasePath">Path to the SQLite database file.</param>
        public SqliteChatHistoryProvider(string databasePath)
            : this(new SqliteChatHistoryProviderOptions { DatabasePath = databasePath })
        {
        }

        /// <inheritdoc/>
        protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var state = _sessionState.GetOrInitializeState(context.Session);
            
            try
            {
                var messages = await _store.LoadMessagesAsync(
                    state.ConversationId,
                    _options.MaxMessagesToLoad,
                    cancellationToken).ConfigureAwait(false);

                // Apply additional filtering if configured
                if (_options.MessageFilter != null)
                {
                    messages = messages.Where(m => _options.MessageFilter(m)).ToList();
                }

                if (_options.LogOperations)
                {
                    Debug.Log($"[SqliteChatHistoryProvider] Loaded {messages.Count} messages for conversation {state.ConversationId}");
                }

                return messages;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SqliteChatHistoryProvider] Failed to load chat history: {ex.Message}");
                
                // Return empty history on error to allow the conversation to continue
                return Enumerable.Empty<ChatMessage>();
            }
        }

        /// <inheritdoc/>
        protected override async ValueTask StoreChatHistoryAsync(
            InvokedContext context,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // Don't store if the invocation failed
            if (context.InvokeException != null)
            {
                if (_options.LogOperations)
                {
                    Debug.LogWarning($"[SqliteChatHistoryProvider] Skipping storage due to invocation exception: {context.InvokeException.Message}");
                }
                return;
            }

            var state = _sessionState.GetOrInitializeState(context.Session);

            try
            {
                // Filter out messages that came from chat history to avoid duplicates
                var newMessages = context.RequestMessages
                    .Where(m => m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory)
                    .Concat(context.ResponseMessages ?? Enumerable.Empty<ChatMessage>())
                    .ToList();

                if (newMessages.Count == 0)
                {
                    return;
                }

                await _store.StoreMessagesAsync(
                    state.ConversationId,
                    newMessages,
                    cancellationToken).ConfigureAwait(false);

                // Update conversation metadata
                state.LastMessageAt = DateTime.UtcNow;
                state.MessageCount += newMessages.Count;
                _sessionState.SaveState(context.Session, state);

                if (_options.LogOperations)
                {
                    Debug.Log($"[SqliteChatHistoryProvider] Stored {newMessages.Count} messages for conversation {state.ConversationId}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SqliteChatHistoryProvider] Failed to store chat history: {ex.Message}");
                
                // Don't rethrow - we don't want to break the conversation flow
                // The history will just be incomplete for this turn
            }
        }

        /// <summary>
        /// Creates a new conversation and returns its ID.
        /// </summary>
        /// <param name="session">The agent session.</param>
        /// <returns>The new conversation ID.</returns>
        public string CreateNewConversation(AgentSession session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            var state = new SessionState
            {
                ConversationId = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow
            };

            _sessionState.SaveState(session, state);
            return state.ConversationId;
        }

        /// <summary>
        /// Gets the current conversation ID for the session.
        /// </summary>
        /// <param name="session">The agent session.</param>
        /// <returns>The conversation ID, or null if not initialized.</returns>
        public string GetCurrentConversationId(AgentSession session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            var state = _sessionState.GetOrInitializeState(session);
            return state.ConversationId;
        }

        /// <summary>
        /// Clears the conversation history for the current session.
        /// </summary>
        /// <param name="session">The agent session.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async ValueTask ClearCurrentConversationAsync(
            AgentSession session,
            CancellationToken cancellationToken = default)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            var state = _sessionState.GetOrInitializeState(session);
            
            await _store.DeleteConversationAsync(state.ConversationId, cancellationToken).ConfigureAwait(false);
            
            state.MessageCount = 0;
            state.LastMessageAt = null;
            _sessionState.SaveState(session, state);

            if (_options.LogOperations)
            {
                Debug.Log($"[SqliteChatHistoryProvider] Cleared conversation {state.ConversationId}");
            }
        }

        /// <summary>
        /// Gets conversation statistics for the current session.
        /// </summary>
        /// <param name="session">The agent session.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Conversation statistics.</returns>
        public async ValueTask<ConversationStats> GetConversationStatsAsync(
            AgentSession session,
            CancellationToken cancellationToken = default)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            var state = _sessionState.GetOrInitializeState(session);
            return await _store.GetConversationStatsAsync(state.ConversationId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Prunes old messages from the current conversation to stay within limits.
        /// </summary>
        /// <param name="session">The agent session.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async ValueTask PruneConversationAsync(
            AgentSession session,
            CancellationToken cancellationToken = default)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            var state = _sessionState.GetOrInitializeState(session);
            await _store.PruneConversationAsync(
                state.ConversationId,
                _options.MaxMessagesPerConversation,
                _options.RetentionDays,
                cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!_disposed)
            {
                _store?.Dispose();
                _disposed = true;
            }
        }

        /// <summary>
        /// Session state stored in the AgentSession.
        /// </summary>
        public sealed class SessionState
        {
            /// <summary>
            /// Gets or sets the unique conversation identifier.
            /// </summary>
            public string ConversationId { get; set; }

            /// <summary>
            /// Gets or sets when the conversation was created.
            /// </summary>
            public DateTime CreatedAt { get; set; }

            /// <summary>
            /// Gets or sets when the last message was sent.
            /// </summary>
            public DateTime? LastMessageAt { get; set; }

            /// <summary>
            /// Gets or sets the approximate message count.
            /// </summary>
            public int MessageCount { get; set; }
        }
    }

    /// <summary>
    /// Configuration options for <see cref="SqliteChatHistoryProvider"/>.
    /// </summary>
    public sealed class SqliteChatHistoryProviderOptions
    {
        /// <summary>
        /// Gets or sets the path to the SQLite database file.
        /// </summary>
        public string DatabasePath { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of messages to load into context.
        /// Default: 50.
        /// </summary>
        public int MaxMessagesToLoad { get; set; } = 50;

        /// <summary>
        /// Gets or sets the maximum number of messages to keep per conversation.
        /// Older messages will be pruned. Set to 0 for unlimited. Default: 1000.
        /// </summary>
        public int MaxMessagesPerConversation { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the number of days to retain messages.
        /// Older messages will be pruned. Set to 0 for unlimited. Default: 30.
        /// </summary>
        public int RetentionDays { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether to log operations for debugging.
        /// Default: false.
        /// </summary>
        public bool LogOperations { get; set; } = false;

        /// <summary>
        /// Gets or sets an optional filter to apply to messages before loading.
        /// </summary>
        public Func<ChatMessage, bool> MessageFilter { get; set; }

        /// <summary>
        /// Gets or sets whether to enable write-ahead logging for better concurrency.
        /// Default: true.
        /// </summary>
        public bool EnableWalMode { get; set; } = true;

        /// <summary>
        /// Gets or sets the connection timeout in seconds.
        /// Default: 30.
        /// </summary>
        public int ConnectionTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets whether to automatically create the database and tables.
        /// Default: true.
        /// </summary>
        public bool AutoCreateDatabase { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable encryption for the database.
        /// Note: Requires SQLite encryption extension.
        /// </summary>
        public bool EnableEncryption { get; set; } = false;

        /// <summary>
        /// Gets or sets the encryption key (if encryption is enabled).
        /// </summary>
        public string EncryptionKey { get; set; }
    }

    /// <summary>
    /// Statistics for a conversation.
    /// </summary>
    public sealed class ConversationStats
    {
        /// <summary>
        /// Gets the conversation ID.
        /// </summary>
        public string ConversationId { get; set; }

        /// <summary>
        /// Gets the total message count.
        /// </summary>
        public int MessageCount { get; set; }

        /// <summary>
        /// Gets the user message count.
        /// </summary>
        public int UserMessageCount { get; set; }

        /// <summary>
        /// Gets the assistant message count.
        /// </summary>
        public int AssistantMessageCount { get; set; }

        /// <summary>
        /// Gets the tool call count.
        /// </summary>
        public int ToolCallCount { get; set; }

        /// <summary>
        /// Gets when the conversation was created.
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// Gets when the last message was sent.
        /// </summary>
        public DateTime? LastMessageAt { get; set; }

        /// <summary>
        /// Gets the approximate token count (if tracked).
        /// </summary>
        public int? ApproximateTokenCount { get; set; }
    }
}
