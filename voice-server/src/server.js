/**
 * GameNight Voice Signaling Server
 * --------------------------------
 * Socket.io room broker for WebRTC voice in the GameNight agent.
 * Never carries audio — only SDP / ICE relay. Deploy separately (Railway/Render).
 */
const { createServer } = require('http');
const { Server } = require('socket.io');

const PORT = process.env.PORT || 3001;
const TEAM_ROOMS = new Set(['UFLL', 'APR']);

const rooms = new Map();

const httpServer = createServer((req, res) => {
  if (req.url === '/health') {
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ status: 'ok', rooms: rooms.size }));
    return;
  }
  res.writeHead(404);
  res.end();
});

const io = new Server(httpServer, {
  cors: { origin: '*', methods: ['GET', 'POST'] },
});

function getOrCreateRoom(roomId) {
  if (!rooms.has(roomId)) rooms.set(roomId, new Map());
  return rooms.get(roomId);
}

function getRoomPeers(roomId) {
  const room = rooms.get(roomId);
  if (!room) return [];
  return [...room.entries()].map(([socketId, info]) => ({ socketId, ...info }));
}

function leaveRoom(socket) {
  const { roomId, peerId, displayName } = socket.data ?? {};
  if (!roomId) return;
  const room = rooms.get(roomId);
  if (!room) return;
  room.delete(socket.id);
  if (room.size === 0) {
    rooms.delete(roomId);
    console.log(`[room] "${roomId}" empty, removed`);
  } else {
    socket.to(roomId).emit('peer:left', { socketId: socket.id, peerId, displayName });
  }
}

/** UFLL/APR → uppercase; any other non-empty code allowed as Custom (max 32). */
function canonicalizeRoom(roomId) {
  const trimmed = String(roomId ?? '').trim();
  if (!trimmed) return { error: 'roomId required' };
  if (trimmed.length > 32) return { error: 'Room code max length is 32' };
  const upper = trimmed.toUpperCase();
  if (TEAM_ROOMS.has(upper)) return { room: upper };
  return { room: trimmed };
}

io.on('connection', (socket) => {
  console.log(`[connect] ${socket.id}`);

  socket.on('room:join', ({ roomId, peerId, displayName } = {}, ack) => {
    if (!peerId) {
      ack?.({ error: 'roomId and peerId required' });
      return;
    }
    const canon = canonicalizeRoom(roomId);
    if (canon.error) {
      ack?.({ error: canon.error });
      return;
    }
    const canonicalRoom = canon.room;

    leaveRoom(socket);
    const room = getOrCreateRoom(canonicalRoom);
    const existingPeers = getRoomPeers(canonicalRoom);

    room.set(socket.id, { peerId, displayName: displayName || peerId });
    socket.data = { roomId: canonicalRoom, peerId, displayName };
    socket.join(canonicalRoom);

    console.log(`[join] "${displayName}" (${peerId}) → room "${canonicalRoom}" (${room.size} peers)`);

    socket.to(canonicalRoom).emit('peer:joined', {
      socketId: socket.id,
      peerId,
      displayName: displayName || peerId,
    });
    ack?.({ peers: existingPeers });
  });

  socket.on('signal:offer', ({ to, offer }) => {
    io.to(to).emit('signal:offer', {
      from: socket.id,
      peerId: socket.data?.peerId,
      displayName: socket.data?.displayName,
      offer,
    });
  });

  socket.on('signal:answer', ({ to, answer }) => {
    io.to(to).emit('signal:answer', { from: socket.id, answer });
  });

  socket.on('signal:ice-candidate', ({ to, candidate }) => {
    io.to(to).emit('signal:ice-candidate', { from: socket.id, candidate });
  });

  socket.on('peer:speaking', ({ speaking }) => {
    const { roomId, peerId, displayName } = socket.data ?? {};
    if (!roomId) return;
    socket.to(roomId).emit('peer:speaking', {
      socketId: socket.id,
      peerId,
      displayName,
      speaking,
    });
  });

  socket.on('room:leave', () => {
    leaveRoom(socket);
    socket.data = {};
  });

  socket.on('disconnect', () => {
    leaveRoom(socket);
    console.log(`[disconnect] ${socket.id}`);
  });
});

httpServer.listen(PORT, () => {
  console.log(`GameNight voice signaling on port ${PORT}`);
});
