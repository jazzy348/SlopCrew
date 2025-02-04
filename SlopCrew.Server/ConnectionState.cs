using SlopCrew.Common;
using SlopCrew.Common.Network;
using SlopCrew.Common.Network.Clientbound;
using SlopCrew.Common.Network.Serverbound;
using System.Security.Cryptography;
using System.Text;
using EmbedIO.WebSockets;
using Serilog;

namespace SlopCrew.Server;

public class ConnectionState {
    public Player? Player;
    public int? LastStage = null;

    public ClientboundPlayerAnimation? QueuedAnimation;
    public Transform? QueuedPositionUpdate;
    public ClientboundPlayerScoreUpdate? QueuedScoreUpdate;
    public ClientboundPlayerVisualUpdate? QueuedVisualUpdate;

    public IWebSocketContext Context;
    public object SendLock;

    public ConnectionState(IWebSocketContext context) {
        this.Context = context;
        this.SendLock = new();
    }

    public void HandlePacket(NetworkPacket msg) {
        var server = Server.Instance;

        // These packets get processed when player is null
        switch (msg) {
            case ServerboundVersion {Version: < Constants.NetworkVersion}:
                // Older version, no thanks
                this.Context.WebSocket.CloseAsync();
                return;

            case ServerboundPing ping:
                HandlePing(ping);
                return;

            case ServerboundPlayerHello enter:
                this.HandleHello(enter, server);
                return;
        }

        if (this.Player is null) {
            Log.Verbose("Received message from {DebugName} without a hello, ignoring", this.DebugName());
            return;
        }

        switch (msg) {
            case ServerboundAnimation animation:
                this.HandleAnimation(animation);
                break;

            case ServerboundPositionUpdate positionUpdate:
                this.HandlePositionUpdate(positionUpdate);
                break;

            case ServerboundScoreUpdate scoreUpdate:
                this.HandleScoreUpdate(scoreUpdate);
                break;

            case ServerboundVisualUpdate visualUpdate:
                this.HandleVisualUpdate(visualUpdate);
                break;
        }
    }

    private void HandlePing(ServerboundPing ping) {
        Server.Instance.Module.SendToContext(this.Context, new ClientboundPong {
            ID = ping.ID
        });
    }

    private void HandleHello(ServerboundPlayerHello enter, Server server) {
        var isBiggestLoser = enter.Player.Character is < -1 or > 26
                             || enter.Player.Outfit is < 0 or > 3
                             || enter.Player.MoveStyle is < 0 or > 5;
        if (isBiggestLoser) {
            this.TonightsBiggestLoser();
            return;
        }

        // Assign a unique ID on first hello
        // Subsequent hellos keep the originally assigned ID
        enter.Player.ID = this.Player?.ID ?? server.GetNextID();
        this.Player = enter.Player;

        // Thanks
        this.Player.Name = PlayerNameFilter.DoFilter(this.Player.Name);

        var hash = SHA256.Create();
        var hashBytes = hash.ComputeHash(Encoding.UTF8.GetBytes(enter.SecretCode));
        var hashString = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        this.Player.IsDeveloper = Constants.SecretCodes.Contains(hashString);

        // Syncs player to other players
        server.TrackConnection(this);

        // Set after we track connection because State:tm:
        this.LastStage = this.Player.Stage;
    }

    private void HandleAnimation(ServerboundAnimation animation) {
        this.QueuedAnimation = new ClientboundPlayerAnimation {
            Player = this.Player!.ID,
            Animation = animation.Animation,
            ForceOverwrite = animation.ForceOverwrite,
            Instant = animation.Instant,
            AtTime = animation.AtTime
        };
    }

    private void HandlePositionUpdate(ServerboundPositionUpdate positionUpdate) {
        for (var i = 0; i < 3; i++) {
            // check to see if packet will crash clients
            if (!float.IsFinite(positionUpdate.Transform.Position[i])
                || !float.IsFinite(positionUpdate.Transform.Velocity[i])) break;
        }

        this.Player!.Transform = positionUpdate.Transform;

        this.QueuedPositionUpdate = positionUpdate.Transform;
        this.QueuedPositionUpdate.Tick = Server.CurrentTick;
    }

    private void HandleScoreUpdate(ServerboundScoreUpdate scoreUpdate) {
        this.QueuedScoreUpdate = new ClientboundPlayerScoreUpdate {
            Player = this.Player!.ID,
            Score = scoreUpdate.Score,
            Multiplier = scoreUpdate.Multiplier
        };
    }

    private void HandleVisualUpdate(ServerboundVisualUpdate visualUpdate) {
        this.QueuedVisualUpdate = new ClientboundPlayerVisualUpdate {
            Player = this.Player!.ID,
            BoostpackEffect = visualUpdate.BoostpackEffect,
            FrictionEffect = visualUpdate.FrictionEffect,
            Spraycan = visualUpdate.Spraycan,
            Phone = visualUpdate.Phone
        };
    }

    public string DebugName() {
        var endpoint = this.Context.RemoteEndPoint.ToString();

        return this.Player != null
                   ? $"{this.Player.Name}({this.Player?.ID})"
                   : $"<player null - {endpoint}>";
    }

    public void RunTick() {
        var module = Server.Instance.Module;

        if (this.QueuedAnimation is not null) {
            module.BroadcastInStage(this.Context, this.QueuedAnimation);
            this.QueuedAnimation = null;
        }

        if (this.QueuedScoreUpdate is not null) {
            module.BroadcastInStage(this.Context, this.QueuedScoreUpdate);
            this.QueuedScoreUpdate = null;
        }

        if (this.QueuedVisualUpdate is not null) {
            module.BroadcastInStage(this.Context, this.QueuedVisualUpdate);
            this.QueuedVisualUpdate = null;
        }
    }

    public void TonightsBiggestLoser() {
        var str = this.Player is not null ? this.Player.Name + $" ({this.Player.ID})" : "no player";
        var ip = this.Context.Headers.Get("X-Forwarded-For") ?? this.Context.RemoteEndPoint.ToString();
        Log.Information("tonights biggest loser is {PlayerID} {IP}", str, ip);
        this.Context.WebSocket.CloseAsync();
    }
}
