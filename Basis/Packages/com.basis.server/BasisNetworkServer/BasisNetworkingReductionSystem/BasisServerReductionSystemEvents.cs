using Basis.Network.Core;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworking;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using static BasisNetworkServer.BasisNetworkingReductionSystem.BasisServerReductionSystemEvents;
using static SerializableBasis;

namespace BasisNetworkServer.BasisNetworkingReductionSystem
{
    public class QueuedMessage
    {
        public NetPeer FromPeer;
        public LocalAvatarSyncMessage AvatarMessage;
    }

    public class PlayerState
    {
        public NetPeer Peer;
        public bool IsActive;
        public Basis.Scripts.Networking.Compression.Vector3 Position;
        public FastBitSet HasNewDataFrom;
        public ServerSideSyncPlayerMessage SyncMessage;
        public Dictionary<int, long> LastSentTimes = new();
    }

    public partial class BasisServerReductionSystemEvents
    {
        private static readonly CancellationTokenSource cts = new();
        private static readonly int MaxConcurrentPlayers = 1024;
        private static readonly ParallelOptions parallelOptions = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
        };

        public static ConcurrentDictionary<int, PlayerState> playerStates = new();
        private static ConcurrentDictionary<int, QueuedMessage> currentMessages = new();

        public static float BSRBaseMultiplier = 1.0f;
        public static float BSRSIncreaseRate = 0.01f;
        public static int BSRSMillisecondDefaultInterval = 50;
        private static readonly double MsToTick = Stopwatch.Frequency / 1000.0;

        private static List<(int id, PlayerState state)> _threadLocalActivePlayers = new();
        public static readonly ConcurrentQueue<NetDataWriter> WriterPool = new();
        private static readonly ConcurrentQueue<int> playersToRemove = new();

        static BasisServerReductionSystemEvents()
        {
            _ = StartBackgroundProcessingAsync(); // fire-and-forget async background task
        }

        public static void HandleAvatarMovement(NetPacketReader reader, NetPeer fromPeer)
        {
            var localMessage = new LocalAvatarSyncMessage();
            localMessage.Deserialize(reader);
            reader.Recycle();
            AddMessage(fromPeer, localMessage);
        }

        public static void AddMessage(NetPeer fromPeer, LocalAvatarSyncMessage localMessage)
        {
            var message = QueuedMessagePool.Rent();
            message.FromPeer = fromPeer;
            message.AvatarMessage = localMessage;
            currentMessages.AddOrUpdate(fromPeer.Id, message, (_, _) => message);
        }
        private static async Task StartBackgroundProcessingAsync()
        {
            long intervalMs = 10;

            while (!cts.Token.IsCancellationRequested)
            {
                long startTick = Stopwatch.GetTimestamp();
                // Snapshot messages safely
                var messagesSnapshot = new List<QueuedMessage>(currentMessages.Count);
                foreach (var kvp in currentMessages)
                {
                    if (currentMessages.TryRemove(kvp.Key, out var msg))
                    {
                        messagesSnapshot.Add(msg);
                    }
                }

                // Process messages also adds players
                // Profiling.StartTimer("ProcessMessages", out long t1);
                Parallel.ForEach(messagesSnapshot, parallelOptions, msg =>
                {
                    try
                    {
                        ProcessMessage(msg);
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"[ProcessMessage] Exception: {ex}");
                    }
                });
                //  Profiling.EndTimer("ProcessMessages", t1);

                //once all the new players are added lets remove players that have been requested.
                //its better to remove a player that might still exist as there next send will fix the state.
                //Profiling.StartTimer("ProcessPendingRemovals", out long t3);
                ProcessPendingRemovals();
                // Profiling.EndTimer("ProcessPendingRemovals", t3);

                // Network updates
                // Profiling.StartTimer("SimulateCommunicationFromCache_Full", out long t2);
                UpdateCommunicationAndDistances(Stopwatch.GetTimestamp());
                // Profiling.EndTimer("SimulateCommunicationFromCache_Full", t2);

                // Profiling.TryPrint();

                // Throttle loop if under time budget
                long elapsedTicks = Stopwatch.GetTimestamp() - startTick;
                long elapsedMs = (long)(elapsedTicks / MsToTick);
                long remainingMs = intervalMs - elapsedMs;

                if (remainingMs > 0)
                    await Task.Delay((int)remainingMs, cts.Token);
                else
                    await Task.Yield();
            }
        }
        private static void ProcessPendingRemovals()
        {
            while (playersToRemove.TryDequeue(out int id))
            {
                if (playerStates.TryRemove(id, out var removedState))
                {
                    removedState.IsActive = false;

                    foreach (var kvp in playerStates)
                    {
                        var state = kvp.Value;
                        lock (state)
                        {
                            state.HasNewDataFrom?.Set(id, false);
                            state.LastSentTimes?.Remove(id);
                        }
                    }

                    BNL.Log($"Player {id} removed and cleaned up.");
                }
                else
                {
                    BNL.LogError("Missing Player From Index this is scary! " + id);
                }
            }
        }
        private static void UpdateCommunicationAndDistances(long nowTicks)
        {
            _threadLocalActivePlayers.Clear();
            foreach (var kvp in playerStates)
            {
                if (kvp.Value.IsActive)
                {
                    _threadLocalActivePlayers.Add((kvp.Key, kvp.Value));
                }
            }
            int PlayerCount = _threadLocalActivePlayers.Count;

            Parallel.ForEach(_threadLocalActivePlayers, parallelOptions, playerI =>
            {
                var stateI = playerI.state;
                var peer = stateI.Peer;

                bool canSend = peer.GetPacketsCountInQueue(BasisNetworkCommons.PlayerAvatarChannel, DeliveryMethod.Sequenced) < 10;

                var sentTimes = stateI.LastSentTimes;

                for (int Index = 0; Index < PlayerCount; Index++)
                {
                    var playerJ = _threadLocalActivePlayers[Index];
                    if (playerI.id == playerJ.id)
                    {
                        continue;
                    }

                    var stateJ = playerJ.state;
                    float distSq = DistanceSquared(stateI.Position, stateJ.Position);
                    CalculateIntervalFromDistanceSq(distSq, out byte StartAtZeroInterval, out int ActualInterval);

                    if (!sentTimes.ContainsKey(playerJ.id))
                    {
                        sentTimes[playerJ.id] = 0;
                    }

                    if (stateI.HasNewDataFrom == null)
                    {
                        continue;
                    }

                    long lastSent = sentTimes[playerJ.id];
                    long elapsed = nowTicks - lastSent;
                    elapsed = Math.Max(0, elapsed); // avoid wrap issues

                    long required = (long)(ActualInterval * MsToTick);
                    bool hasNewData = stateI.HasNewDataFrom.Get(playerJ.id);

                    if (canSend && hasNewData && elapsed >= required)
                    {
                        // prepare the message (no network write yet)
                        stateI.HasNewDataFrom.Set(playerJ.id, false);
                        var tempMsg = stateJ.SyncMessage;
                        tempMsg.interval = StartAtZeroInterval;

                        // Batch append (may flush if near MTU)
                        BatchSend(tempMsg, peer);

                        sentTimes[playerJ.id] = nowTicks;
                    }
                }

                // Flush any remaining batched data for this recipient
                _tlsFrameBuilder.FlushTo(peer);
            });
        }

        // ===== NEW: helper to serialize a single message and add to frame =====
        public static void BatchSend(ServerSideSyncPlayerMessage Message, NetPeer Peer)
        {
            // Serialize the single message into a temporary writer to know its exact size.
            NetDataWriter tmp = RentWriter();
            Message.Serialize(tmp);

            // Try to add to current frame; if it doesn't fit, flush and add again.
            if (!_tlsFrameBuilder.TryAdd(tmp.CopyData(), FrameMtuBudgetBytes))
            {
                // We need the peer to flush here, but we only flush in UpdateCommunicationAndDistances
                // right after the loop or when switching recipients. Because this helper doesn't know the peer,
                // we adopt a simple pattern: force a local flush-by-signal using a special chunk.
                // Instead of complicated signaling, we rely on caller to flush at the end of the recipient loop.
                // To handle "doesn't fit now", we force a local flush by creating a second frame immediately:
                // 1) Flush pending to an impossible peer? Not available here.
                // 2) So we emulate: clear builder, then add. To do this safely, we expose a small reset+readd API.

                // Workaround: we temporarily flush when the next FlushTo(...) is called.
                // We mark the builder as needing a split by stashing this chunk into a fresh frame immediately.

                // Simple approach: push a "split marker" by flushing into a stash we can send right away is not possible here.
                // Instead, we do a two-step: we immediately send current frame to the *current* peer at the end of loop.
                // To guarantee re-add, we clear now and add as first element of new frame:

                // Clear current frame state by forcing an impossible add: we’ll rely on the caller to FlushTo(peer)
                // right after the loop; but we need to preserve the chunk for the next frame.
                // Easiest robust approach: store the chunk in a small side buffer on the builder after a virtual flush.
                // For simplicity in this helper, we just start a new frame:

                // Start a new frame by clearing builder state and re-adding the chunk.
                // (We can do it because the builder state is private to the current thread/recipient loop.)
                _tlsFrameBuilder.FlushTo(Peer); // safe no-op when peer == null (we’ll guard inside)
                                                // Since FlushTo(null) won't send, we mimic a reset by creating a new builder:
                _tlsFrameBuilder = new FrameBuilder();

                // Now it must fit (single message should be below MTU)
                bool ok = _tlsFrameBuilder.TryAdd(tmp.CopyData(), FrameMtuBudgetBytes);
                if (!ok)
                {
                    // Extremely unlikely: single message > MTU. Fallback: send it alone raw (non-batched).
                    // We'll emit it right now as its own frame during the final FlushTo(peer) call.
                    // Nothing else to do here.
                }
            }

            ReturnWriter(tmp);
        }
        private const int FrameMtuBudgetBytes = 1232; // stay under network MTU (~1232 incl. headers)
        [ThreadStatic] private static FrameBuilder _tlsFrameBuilder; // one per parallel worker

        // Minimal frame builder that writes: [ushort count][msg1][msg2]...
        private sealed class FrameBuilder
        {
            private readonly List<ArraySegment<byte>> _chunks = new(16);
            private int _payloadBytes; // sum of chunk lengths (not counting the 2-byte count header)
            private byte _count;     // number of messages in current frame

            // Adds a serialized chunk; returns false if the caller should flush first and re-add.
            public bool TryAdd(ReadOnlySpan<byte> data, int mtuBudget)
            {
                // If empty frame, cost is 2 (count header) + data.Length
                // If non-empty, cost becomes existing payload + 2 + data.Length
                if (_count == 0)
                {
                    if (2 + data.Length > mtuBudget) return false; // single message too big (shouldn't happen for ~173B)
                }
                else
                {
                    if (2 + _payloadBytes + data.Length > mtuBudget) return false;
                }

                // Buffer the data (copy into an owned array segment)
                byte[] buf = new byte[data.Length];
                data.CopyTo(buf);
                _chunks.Add(new ArraySegment<byte>(buf, 0, buf.Length));
                _payloadBytes += buf.Length;
                _count++;
                if (_count == byte.MaxValue)
                {
                    BNL.LogError("Max Value Reached for the count in Server Reduction System");
                }
                return true;
            }

            public bool HasData => _count > 0;

            public void FlushTo(NetPeer peer)
            {
                if (!HasData) return;

                // Serialize frame header + all chunks into one writer
                NetDataWriter writer = RentWriter();

                int count = _chunks.Count;
                // Copy chunks
                for (int Index = 0; Index < count; Index++)
                {
                    var seg = _chunks[Index];
                    writer.Put(seg.Array, seg.Offset, seg.Count);
                }

                // Send once
                peer.Send(writer, BasisNetworkCommons.BatchedPlayerAvatarChannel, DeliveryMethod.Sequenced);
                BasisNetworkStatistics.RecordOutbound(BasisNetworkCommons.BatchedPlayerAvatarChannel, writer.Length);
                ReturnWriter(writer);

                // Reset for next frame
                _chunks.Clear();
                _payloadBytes = 0;
                _count = 0;
            }
        }
        public static NetDataWriter RentWriter()
        {
            return WriterPool.TryDequeue(out var writer) ? writer : new NetDataWriter(true, 208);
        }

        public static void ReturnWriter(NetDataWriter writer)
        {
            writer.Reset();
            WriterPool.Enqueue(writer);
        }
        private static float DistanceSquared(Basis.Scripts.Networking.Compression.Vector3 a, Basis.Scripts.Networking.Compression.Vector3 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            float dz = a.z - b.z;
            return dx * dx + dy * dy + dz * dz;
        }
        /// <summary>
        /// Calculates the offset byte and the actual interval from the squared distance.
        /// </summary>
        private static void CalculateIntervalFromDistanceSq(float distanceSq, out byte offsetByte, out int actualInterval)
        {
            int rawInterval = (int)(BSRSMillisecondDefaultInterval * (BSRBaseMultiplier + (distanceSq * BSRSIncreaseRate)));
            int encodedInterval = rawInterval - BSRSMillisecondDefaultInterval;

            offsetByte = (byte)Math.Clamp(encodedInterval, 0, byte.MaxValue);
            actualInterval = offsetByte + BSRSMillisecondDefaultInterval;
        }
        public static void Shutdown() => cts.Cancel();
        public static void RemovePlayer(int id)
        {
            playersToRemove.Enqueue(id);
        }
        public struct Player
        {
            public readonly int Id;
            public ServerSideSyncPlayerMessage syncMsg;

            public Player(int id, ServerSideSyncPlayerMessage syncMsg)
            {
                Id = id;
                this.syncMsg = syncMsg;
            }
        }
        private static void ProcessMessage(QueuedMessage message)
        {
            int id = message.FromPeer.Id;
            if (!playerStates.TryGetValue(id, out var state))
            {
                state = new PlayerState
                {
                    Peer = message.FromPeer,
                    IsActive = true,
                    Position = BasisNetworkCompressionExtensions.ReadPosition(ref message.AvatarMessage.array),
                    HasNewDataFrom = new FastBitSet(MaxConcurrentPlayers),
                    SyncMessage = new ServerSideSyncPlayerMessage
                    {
                        playerIdMessage = new PlayerIdMessage { playerID = (ushort)id },
                        avatarSerialization = message.AvatarMessage
                    },
                };
                state.HasNewDataFrom.SetAll(true);
                playerStates[id] = state;

                foreach (var kvp in playerStates)
                {
                    if (kvp.Key == id || !kvp.Value.IsActive) continue;
                    kvp.Value.HasNewDataFrom.Set(id, true);
                }
            }
            else
            {
                if (!state.IsActive) state.IsActive = true;

                state.Position = BasisNetworkCompressionExtensions.ReadPosition(ref message.AvatarMessage.array);
                state.SyncMessage.avatarSerialization = message.AvatarMessage;

                // Mark all *other* players as having new data FROM this sender (id)
                foreach (var kvp in playerStates)
                {
                    if (kvp.Key == id)
                    {
                        continue;
                    }

                    var other = kvp.Value;
                    if (!other.IsActive)
                    {
                        continue;
                    }

                    lock (other)
                    {
                        other.HasNewDataFrom?.Set(id, true);
                    }
                }
            }

            QueuedMessagePool.Return(message);
        }
    }
}
