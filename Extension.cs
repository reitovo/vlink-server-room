using System.Collections.Concurrent;
using Grpc.Core;
using VLink.Private.GrpcRoomServer.Services;
using Vts.Server;
using Peer = VLink.Private.GrpcRoomServer.Services.Peer;

namespace VLink.Private.GrpcRoomServer;

public static class Extension {
   public static (string roomId, string peerId) RoomPeerId(this ServerCallContext ctx) {
      var roomId = ctx.RequestHeaders.GetValue("roomId");
      var peerId = ctx.RequestHeaders.GetValue("peerId");
      if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(peerId))
         throw new RpcException(new Status(StatusCode.Unauthenticated, "roomId or peerId is empty"));
      return (roomId, peerId);
   }

   public static (Room room, Peer peer) RoomPeer(this ServerCallContext ctx, ConcurrentDictionary<string, Room> rooms) {
      var (roomId, peerId) = ctx.RoomPeerId();
      if (!rooms.TryGetValue(roomId, out var room))
         throw new RpcException(new Status(StatusCode.NotFound, "room not found"));
      if (!room.Peers.TryGetValue(peerId, out var peer))
         throw new RpcException(new Status(StatusCode.NotFound, "room not found"));
      return (room, peer);
   }

   public static (Room room, Peer peer) RoomPeerVerifyCreator(this ServerCallContext ctx,
      ConcurrentDictionary<string, Room> rooms) {
      var (roomId, peerId) = ctx.RoomPeerId();
      if (!rooms.TryGetValue(roomId, out var room))
         throw new RpcException(new Status(StatusCode.NotFound, "room not found"));
      if (!room.Peers.TryGetValue(peerId, out var peer))
         throw new RpcException(new Status(StatusCode.NotFound, "room not found"));
      if (room.CreatorPeerId != peer.PeerId) {
         throw new RpcException(new Status(StatusCode.PermissionDenied, "requester not creator"));
      }
      return (room, peer);
   }

   public static void BroadcastPeersList(this Room room) {
      foreach (var p in room.Peers) {
         p.Value.Notifier?.Enqueue(new Notify() {
            Peers = room.PeersList
         });
      }
   }

   public static void BroadcastFrameFormat(this Room room) {
      foreach (var p in room.Peers) {
         p.Value.Notifier?.Enqueue(new Notify() {
            Frame = room.Format
         });
      }
   }

   public static void BroadcastRoomDestroy(this Room room) {
      foreach (var p in room.Peers.Where(a => !a.Value.IsServer)) {
         p.Value.Notifier?.Enqueue(new Notify() {
            RoomDestroy = new NotifyCommon()
         });
      }
   }

   public static void BroadcastForceIdr(this Room room) {
      foreach (var p in room.Peers) {
         p.Value.Notifier?.Enqueue(new Notify() {
            ForceIdr = new NotifyCommon()
         });
      }
   }
}