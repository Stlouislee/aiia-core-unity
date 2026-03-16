using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using UnityEngine;

namespace LiveLink.Agent
{
    /// <summary>
    /// Persists chat history to local files and restores it per conversation.
    /// </summary>
    internal sealed class FileChatHistoryProvider : ChatHistoryProvider
    {
        private const int CurrentSchemaVersion = 1;
        private const string DefaultConversationId = "default";

        private readonly ProviderSessionState<ProviderState> _providerState;
        private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);
        private readonly string _storageDirectory;
        private readonly string _conversationIdOverride;
        private readonly int _maxPersistedMessages;
        private readonly int _maxFileSizeBytes;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        internal FileChatHistoryProvider(
            string storageDirectory,
            string conversationIdOverride,
            int maxPersistedMessages,
            int maxFileSizeBytes)
            : base(null, null, null)
        {
            _storageDirectory = string.IsNullOrWhiteSpace(storageDirectory)
                ? Path.Combine(Application.persistentDataPath, "LiveLink", "AgentHistory")
                : storageDirectory;

            _conversationIdOverride = string.IsNullOrWhiteSpace(conversationIdOverride)
                ? DefaultConversationId
                : conversationIdOverride.Trim();

            _maxPersistedMessages = Math.Max(10, maxPersistedMessages);
            _maxFileSizeBytes = Math.Max(16 * 1024, maxFileSizeBytes);

            _providerState = new ProviderSessionState<ProviderState>(
                stateInitializer: _ => new ProviderState { ConversationId = _conversationIdOverride },
                stateKey: GetType().FullName ?? nameof(FileChatHistoryProvider),
                jsonSerializerOptions: JsonOptions);

            Directory.CreateDirectory(_storageDirectory);
        }

        protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            string conversationId = GetConversationId(context.Session);

            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ChatHistoryDocument document = await ReadDocumentAsync(conversationId, cancellationToken).ConfigureAwait(false);
                if (document.Messages == null || document.Messages.Count == 0)
                {
                    return Array.Empty<ChatMessage>();
                }

                var restored = new List<ChatMessage>(document.Messages.Count);
                for (int i = 0; i < document.Messages.Count; i++)
                {
                    PersistedChatMessage persisted = document.Messages[i];
                    if (persisted == null)
                    {
                        continue;
                    }

                    if (TryDeserializeRawMessage(persisted.RawMessageJson, out ChatMessage rawMessage))
                    {
                        restored.Add(rawMessage);
                        continue;
                    }

                    restored.Add(CreateFallbackMessage(persisted));
                }

                return restored;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            if (context.InvokeException != null)
            {
                return;
            }

            string conversationId = GetConversationId(context.Session);
            IEnumerable<ChatMessage> responseMessages = context.ResponseMessages ?? Array.Empty<ChatMessage>();
            List<ChatMessage> allNewMessages = context.RequestMessages.Concat(responseMessages).ToList();
            if (allNewMessages.Count == 0)
            {
                return;
            }

            await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ChatHistoryDocument document = await ReadDocumentAsync(conversationId, cancellationToken).ConfigureAwait(false);
                document.ConversationId = conversationId;
                document.SchemaVersion = CurrentSchemaVersion;
                if (document.CreatedUtc == default)
                {
                    document.CreatedUtc = DateTime.UtcNow;
                }

                foreach (ChatMessage message in allNewMessages)
                {
                    document.Messages.Add(PersistedChatMessage.FromChatMessage(message));
                }

                TrimHistory(document.Messages, _maxPersistedMessages);
                document.UpdatedUtc = DateTime.UtcNow;

                await WriteDocumentAtomicAsync(document, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private string GetConversationId(AgentSession session)
        {
            ProviderState state = _providerState.GetOrInitializeState(session);
            string targetId = string.IsNullOrWhiteSpace(_conversationIdOverride)
                ? state.ConversationId
                : _conversationIdOverride;

            if (string.IsNullOrWhiteSpace(targetId))
            {
                targetId = DefaultConversationId;
            }

            if (!string.Equals(state.ConversationId, targetId, StringComparison.Ordinal))
            {
                state.ConversationId = targetId;
                _providerState.SaveState(session, state);
            }

            return targetId;
        }

        private static void TrimHistory(List<PersistedChatMessage> messages, int maxPersistedMessages)
        {
            if (messages.Count <= maxPersistedMessages)
            {
                return;
            }

            int removeCount = messages.Count - maxPersistedMessages;
            messages.RemoveRange(0, removeCount);
        }

        private async Task<ChatHistoryDocument> ReadDocumentAsync(string conversationId, CancellationToken cancellationToken)
        {
            string filePath = GetFilePath(conversationId);
            if (!File.Exists(filePath))
            {
                return ChatHistoryDocument.CreateNew(conversationId);
            }

            FileInfo fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > _maxFileSizeBytes)
            {
                Debug.LogWarning($"[LiveLink-Agent] Chat history file is larger than configured limit ({_maxFileSizeBytes} bytes). Trimming after load. File: {filePath}");
            }

            try
            {
                string json;
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    json = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                ChatHistoryDocument document = JsonSerializer.Deserialize<ChatHistoryDocument>(json, JsonOptions);
                if (document == null)
                {
                    return ChatHistoryDocument.CreateNew(conversationId);
                }

                if (document.Messages == null)
                {
                    document.Messages = new List<PersistedChatMessage>();
                }

                if (string.IsNullOrWhiteSpace(document.ConversationId))
                {
                    document.ConversationId = conversationId;
                }

                TrimHistory(document.Messages, _maxPersistedMessages);
                return document;
            }
            catch (Exception ex)
            {
                string corruptPath = filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                try
                {
                    File.Move(filePath, corruptPath);
                }
                catch
                {
                    // Best effort backup of corrupt content.
                }

                Debug.LogWarning($"[LiveLink-Agent] Failed to parse chat history file, starting a new history. File: {filePath}. Error: {ex.Message}");
                return ChatHistoryDocument.CreateNew(conversationId);
            }
        }

        private async Task WriteDocumentAtomicAsync(ChatHistoryDocument document, CancellationToken cancellationToken)
        {
            string filePath = GetFilePath(document.ConversationId);
            string tempPath = filePath + ".tmp";
            string backupPath = filePath + ".bak";

            Directory.CreateDirectory(_storageDirectory);

            string json = JsonSerializer.Serialize(document, JsonOptions);
            byte[] payload = Encoding.UTF8.GetBytes(json);
            if (payload.Length > _maxFileSizeBytes)
            {
                Debug.LogWarning($"[LiveLink-Agent] Chat history exceeds max file size ({_maxFileSizeBytes} bytes). Oldest entries were already trimmed by count; consider lowering payload size or increasing the limit.");
            }

            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(payload, 0, payload.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private string GetFilePath(string conversationId)
        {
            string normalizedId = string.IsNullOrWhiteSpace(conversationId)
                ? DefaultConversationId
                : conversationId.Trim();
            string hash = ComputeSha256Hex(normalizedId);
            return Path.Combine(_storageDirectory, hash + ".json");
        }

        private static string ComputeSha256Hex(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }
            var builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }

            return builder.ToString();
        }

        private static bool TryDeserializeRawMessage(string rawJson, out ChatMessage message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return false;
            }

            try
            {
                message = JsonSerializer.Deserialize<ChatMessage>(rawJson, JsonOptions);
                return message != null;
            }
            catch
            {
                return false;
            }
        }

        private static ChatMessage CreateFallbackMessage(PersistedChatMessage persisted)
        {
            string roleValue = string.IsNullOrWhiteSpace(persisted.Role) ? ChatRole.User.Value : persisted.Role;
            var message = new ChatMessage(new ChatRole(roleValue), persisted.Text ?? string.Empty)
            {
                AuthorName = persisted.AuthorName,
                CreatedAt = persisted.CreatedAt,
                MessageId = persisted.MessageId
            };

            return message;
        }

        [Serializable]
        private sealed class ProviderState
        {
            public string ConversationId;
        }

        [Serializable]
        private sealed class ChatHistoryDocument
        {
            public int SchemaVersion = CurrentSchemaVersion;
            public string ConversationId;
            public DateTime CreatedUtc;
            public DateTime UpdatedUtc;
            public List<PersistedChatMessage> Messages = new List<PersistedChatMessage>();

            public static ChatHistoryDocument CreateNew(string conversationId)
            {
                DateTime now = DateTime.UtcNow;
                return new ChatHistoryDocument
                {
                    SchemaVersion = CurrentSchemaVersion,
                    ConversationId = conversationId,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                    Messages = new List<PersistedChatMessage>()
                };
            }
        }

        [Serializable]
        private sealed class PersistedChatMessage
        {
            public string Role;
            public string Text;
            public string AuthorName;
            public DateTimeOffset? CreatedAt;
            public string MessageId;
            public string RawMessageJson;

            public static PersistedChatMessage FromChatMessage(ChatMessage message)
            {
                return new PersistedChatMessage
                {
                    Role = message?.Role.Value,
                    Text = message?.Text,
                    AuthorName = message?.AuthorName,
                    CreatedAt = message?.CreatedAt,
                    MessageId = message?.MessageId,
                    RawMessageJson = TrySerializeMessage(message)
                };
            }

            private static string TrySerializeMessage(ChatMessage message)
            {
                if (message == null)
                {
                    return null;
                }

                try
                {
                    return JsonSerializer.Serialize(message, JsonOptions);
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}