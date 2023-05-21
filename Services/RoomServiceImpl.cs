using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Vts.Server;

namespace VLink.Private.GrpcRoomServer.Services;

public class Notifier {
   private readonly CancellationTokenSource _linkedToken;
   private readonly CancellationTokenSource _notifierCanceller = new();
   private readonly IServerStreamWriter<Notify> _notifierStream;
   private readonly Queue<Notify> _notifierQueue = new();

   public bool IsCancelled => _linkedToken.IsCancellationRequested;
   public CancellationToken CancellationToken => _linkedToken.Token;

   public Notifier(IServerStreamWriter<Notify> stream, CancellationToken token) {
      _notifierStream = stream;
      _linkedToken = CancellationTokenSource.CreateLinkedTokenSource(_notifierCanceller.Token, token);
   }

   public void Enqueue(Notify notify) {
      _notifierQueue.Enqueue(notify);
   }

   public async Task SendAsync() {
      while (_notifierQueue.Count != 0) {
         var notify = _notifierQueue.Dequeue();
         await _notifierStream.WriteAsync(notify, _linkedToken.Token);
      }
   }

   public void Cancel() {
      _notifierCanceller.Cancel();
   }
}

public class Peer {
   public required string PeerId { get; set; }
   public required bool IsServer { get; set; }
   public required string Nick { get; set; }
   public long Rtt { get; set; }
   public int NatType { get; set; }
   public int SortingOrder { get; set; }
   public Notifier Notifier { get; set; }
}

public class Room {
   public required string RoomId { get; set; }
   public required string CreatorPeerId { get; set; }
   public FrameFormatSetting Format { get; set; }
   public FrameFormatSetting MaxFormat { get; set; }
   public ConcurrentDictionary<string, Peer> Peers { get; } = new();
   public NotifyPeers PeersList {
      get {
         var ret = new NotifyPeers();
         var list = Peers.Values.ToList();
         list.Sort((a, b) => b.SortingOrder == a.SortingOrder
            ? b.IsServer.CompareTo(a.IsServer)
            : b.SortingOrder.CompareTo(a.SortingOrder));
         ret.Peers.AddRange(list.Select(a => new Vts.Server.Peer() {
            IsServer = a.IsServer,
            NatType = a.NatType,
            Nick = a.Nick,
            PeerId = a.PeerId,
            Rtt = a.Rtt,
            SortingOrder = a.SortingOrder
         }));
         return ret;
      }
   }
}

public class RoomServiceImpl : RoomService.RoomServiceBase {
   private readonly ILogger<RoomServiceImpl> _logger;

   public RoomServiceImpl(ILogger<RoomServiceImpl> logger) {
      _logger = logger;
   }

   private static readonly ConcurrentDictionary<string, Room> Rooms = new();

   private void SafeRemovePeerFromRoom(Room room, Peer peer) {
      room.Peers.TryRemove(peer.PeerId, out _);
      if (peer.IsServer) {
         room.BroadcastRoomDestroy();
         Rooms.TryRemove(room.RoomId, out _);
      } else {
         peer.Notifier.Cancel();
         room.BroadcastPeersList();
      }
   }

   public override Task<RspRoomInfo> CreateRoom(ReqCreateRoom request, ServerCallContext context) {
      var roomId = Guid.NewGuid().ToString().ToLower();
      var room = new Room() {
         RoomId = roomId,
         CreatorPeerId = request.PeerId,
         Format = request.Format
      };
      Rooms[roomId] = room;
      var peer = new Peer() {
         PeerId = request.PeerId,
         Nick = request.Nick,
         IsServer = true
      };
      room.Peers[peer.PeerId] = peer;
      var resp = new RspRoomInfo() {
         RoomId = roomId,
         Format = room.Format
      };
      _logger.LogTrace($"Create Room {roomId} {request.PeerId}");
      return Task.FromResult(resp);
   }

   public override Task<RspRoomInfo> JoinRoom(ReqJoinRoom request, ServerCallContext context) {
      if (!Rooms.TryGetValue(request.RoomId, out var room))
         throw new RpcException(new Status(StatusCode.NotFound, "room not found"));

      var peer = new Peer() {
         PeerId = request.PeerId,
         Nick = request.Nick,
         IsServer = false
      };
      room.Peers[peer.PeerId] = peer;
      var resp = new RspRoomInfo() {
         RoomId = room.RoomId,
         Format = room.Format
      };
      _logger.LogTrace($"Join Room {request.RoomId} {request.PeerId}");
      return Task.FromResult(resp);
   }

   public override async Task ReceiveNotify(ReqCommon request, IServerStreamWriter<Notify> responseStream,
      ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      var notifier = peer.Notifier = new(responseStream, context.CancellationToken);
      notifier.Enqueue(new Notify() {
         Peers = room.PeersList
      });
      while (!notifier.IsCancelled) {
         try {
            await Task.Delay(TimeSpan.FromMilliseconds(10), notifier.CancellationToken);
            await peer.Notifier.SendAsync();
         } catch (TaskCanceledException) {
            _logger.LogInformation($"Notify Loop Exit {room} {peer}");
            SafeRemovePeerFromRoom(room, peer);
         } catch (Exception ex) {
            _logger.LogError(ex, "Notify Loop Error");
            SafeRemovePeerFromRoom(room, peer);
         }
      }
   }

   public override Task<RspCommon> SetNatType(ReqNatType request, ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      peer.NatType = request.NatType;
      room.BroadcastPeersList();
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetRtt(ReqRtt request, ServerCallContext context) {
      var (room, _) = context.RoomPeer(Rooms);

      foreach (var r in request.Rtt) {
         if (room.Peers.TryGetValue(r.Key, out var p)) {
            p.Rtt = r.Value;
         }
      }
      room.BroadcastPeersList();
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> RequestIdr(ReqCommon request, ServerCallContext context) {
      var (room, _) = context.RoomPeer(Rooms);
      room.BroadcastForceIdr();
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetSdp(Sdp request, ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);

      if (request.FromPeerId != peer.PeerId) {
         throw new RpcException(new Status(StatusCode.InvalidArgument, "from peer not match"));
      }
      if (!room.Peers.TryGetValue(request.ToPeerId, out var to)) {
         throw new RpcException(new Status(StatusCode.NotFound, "to peer not found"));
      }

      _logger.LogTrace($"Sdp {room.RoomId} {request}");

      to.Notifier.Enqueue(new Notify() {
         Sdp = request
      });
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetNickName(ReqNickname request, ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      peer.Nick = request.Nick;
      room.BroadcastPeersList();
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetFrameFormat(FrameFormatSetting request, ServerCallContext context) {
      var (room, peer) = context.RoomPeerVerifyCreator(Rooms);

      if (room.MaxFormat != null) {
         _logger.LogTrace($"quality req = {request} max = {room.MaxFormat}");
         request.FrameHeight = Math.Min(request.FrameHeight, room.MaxFormat.FrameHeight);
         request.FrameWidth = Math.Min(request.FrameWidth, room.MaxFormat.FrameWidth);
         request.FrameQuality = Math.Min(request.FrameQuality, room.MaxFormat.FrameQuality);
         request.FrameRate = Math.Min(request.FrameRate, room.MaxFormat.FrameRate);
      }

      room.Format = request;
      room.BroadcastFrameFormat();
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> Exit(ReqCommon request, ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      SafeRemovePeerFromRoom(room, peer);
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> Hello(ReqCommon request, ServerCallContext context) {
      var ret = new RspCommon();
      return Task.FromResult(ret);
   }
}