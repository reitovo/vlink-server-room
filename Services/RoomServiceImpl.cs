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
   public string Gpu { get; set; }
   public string Capture { get; set; }
   public bool Sharing { get; set; }
   public bool IsWireless { get; set; }
   public bool Is2G4Wireless { get; set; }
   public long RxSpeed { get; set; }
   public long TxSpeed { get; set; }
   public long RxBytes { get; set; }
   public long TxBytes { get; set; }
}

public class Room {
   public required string RoomId { get; set; }
   public required string CreatorPeerId { get; set; }
   public required DateTimeOffset CreateTime { get; set; }
   public FrameFormatSetting Format { get; set; }
   public FrameFormatSetting MaxFormat { get; set; }
   public ConcurrentDictionary<string, Peer> Peers { get; } = new();
   public string Turn { get; set; }

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

public class RoomServiceImpl(ILogger<RoomServiceImpl> _logger) : RoomService.RoomServiceBase {
   public static readonly ConcurrentDictionary<string, Room> Rooms = new();

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
      _logger.LogTrace($"Create Room {roomId} {request}");

      Room room;
      if (request.HasReclaimRoomId) {
         if (!Rooms.TryGetValue(request.ReclaimRoomId, out room)) {
            throw new RpcException(new Status(StatusCode.NotFound, "reclaim room not found"));
         }
      } else {
         room = new Room() {
            RoomId = roomId,
            CreatorPeerId = request.PeerId,
            CreateTime = DateTimeOffset.Now,
            Format = request.Format
         };
         Rooms[roomId] = room;
      }

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

      if (!string.IsNullOrWhiteSpace(request.Turn)) {
         room.Turn = request.Turn;
      }

      return Task.FromResult(resp);
   }

   public override Task<RspRoomInfo> JoinRoom(ReqJoinRoom request, ServerCallContext context) {
      if (!Rooms.TryGetValue(request.RoomId, out var room))
         throw new RpcException(new Status(StatusCode.NotFound, "room not found"));
      _logger.LogTrace($"Join Room {request}");

      var peer = new Peer() {
         PeerId = request.PeerId,
         Nick = request.Nick,
         IsServer = false
      };
      room.Peers[peer.PeerId] = peer;
      var resp = new RspRoomInfo() {
         RoomId = room.RoomId,
         Format = room.Format,
         HostPeerId = room.CreatorPeerId,
         Turn = room.Turn ?? string.Empty
      };
      return Task.FromResult(resp);
   }

   public async override Task ReceiveNotify(ReqCommon request, IServerStreamWriter<Notify> responseStream,
      ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      _logger.LogTrace($"Receive Notify {room.RoomId} {peer.PeerId} {request}");
      var notifier = peer.Notifier = new(responseStream, context.CancellationToken);
      notifier.Enqueue(new Notify() {
         Peers = room.PeersList
      });
      while (!notifier.IsCancelled) {
         try {
            await Task.Delay(TimeSpan.FromMilliseconds(5), notifier.CancellationToken);
            await peer.Notifier.SendAsync();
         } catch (TaskCanceledException) {
            _logger.LogInformation($"Notify Loop Exit {room.RoomId} {peer.PeerId}");
            SafeRemovePeerFromRoom(room, peer);
         } catch (Exception ex) {
            _logger.LogError(ex, "Notify Loop Error");
            SafeRemovePeerFromRoom(room, peer);
         }
      }
   }

   public override Task<RspCommon> SetTurn(TurnInfo request, ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      _logger.LogTrace($"Set Turn {room.RoomId} {request}");


      if (!string.IsNullOrWhiteSpace(request.Turn)) {
         room.Turn = request.Turn;
      }

      room.BroadcastTurn();

      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetStat(ReqStat request, ServerCallContext context) {
      var (room, _) = context.RoomPeer(Rooms);
      _logger.LogTrace($"Set Rtt {room.RoomId} {request}");

      foreach (var r in request.Stats) {
         if (room.Peers.TryGetValue(r.Key, out var p)) {
            p.RxBytes = (long)r.Value.RxBytes;
            p.TxBytes = (long)r.Value.TxBytes;
            p.RxSpeed = (long)r.Value.RxSpeed;
            p.TxSpeed = (long)r.Value.TxSpeed;
            p.Rtt = r.Value.Rtt;
         }
      }

      room.BroadcastPeersList();
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> RequestIdr(ReqIdr request, ServerCallContext context) {
      var (room, _) = context.RoomPeer(Rooms);
      _logger.LogTrace($"Request Idr {room.RoomId} {request}");

      if (room.Peers.TryGetValue(request.PeerId, out var to)) {
         to.Notifier.Enqueue(new Notify() {
            ForceIdr = new NotifyCommon()
         });
      } else {
         _logger.LogError("to peer not found");
      }

      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetNatType(ReqNatType request, ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      _logger.LogTrace($"Set Nat Type {room.RoomId} {peer.PeerId} {request}");
      peer.NatType = request.NatType;
      room.BroadcastPeersList();
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetShareInfo(ReqShareInfo request, ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      _logger.LogTrace($"Set ShareInfo {room.RoomId} {peer.PeerId} {request}");
      peer.Gpu = request.Gpu;
      peer.Capture = request.Capture;
      peer.Sharing = request.Start;
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetRtt(ReqRtt request, ServerCallContext context) {
      var (room, _) = context.RoomPeer(Rooms);
      _logger.LogTrace($"Set Rtt {room.RoomId} {request}");

      foreach (var r in request.Rtt) {
         if (room.Peers.TryGetValue(r.Key, out var p)) {
            p.Rtt = r.Value;
         }
      }

      room.BroadcastPeersList();
      return Task.FromResult(new RspCommon());
   }


   public override Task<RspCommon> SetSdp(Sdp request, ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      _logger.LogTrace($"Set Sdp {room.RoomId} {request}");

      if (request.FromPeerId != peer.PeerId) {
         throw new RpcException(new Status(StatusCode.InvalidArgument, "from peer not match"));
      }

      if (room.Peers.TryGetValue(request.ToPeerId, out var to)) {
         to.Notifier.Enqueue(new Notify() {
            Sdp = request
         });
      } else {
         _logger.LogError("to peer not found");
      }

      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetCandidate(Candidate request, ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      _logger.LogTrace($"Set Candidate {room.RoomId} {request}");

      if (request.FromPeerId != peer.PeerId) {
         throw new RpcException(new Status(StatusCode.InvalidArgument, "from peer not match"));
      }

      if (room.Peers.TryGetValue(request.ToPeerId, out var to)) {
         to.Notifier.Enqueue(new Notify() {
            Candidate = request
         });
      } else {
         _logger.LogError("to peer not found");
      }

      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetNickName(ReqNickname request, ServerCallContext context) {
      var (room, peer) = context.RoomPeer(Rooms);
      _logger.LogTrace($"Set NickName {room.RoomId} {peer.PeerId} {request}");

      peer.Nick = request.Nick;
      room.BroadcastPeersList();
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> SetFrameFormat(FrameFormatSetting request, ServerCallContext context) {
      var (room, _) = context.RoomPeerVerifyCreator(Rooms);
      _logger.LogTrace($"Set Frame Format {room.RoomId} {request}");

      if (room.MaxFormat != null) {
         _logger.LogTrace($"Quality Max = {room.MaxFormat}");
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
      _logger.LogTrace($"Exit {room.RoomId} {peer.PeerId} {request}");

      SafeRemovePeerFromRoom(room, peer);
      return Task.FromResult(new RspCommon());
   }

   public override Task<RspCommon> Hello(ReqCommon request, ServerCallContext context) {
      var ret = new RspCommon();
      return Task.FromResult(ret);
   }
}