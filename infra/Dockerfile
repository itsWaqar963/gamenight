# Multi-stage build (SDD §26). Phase 1: the build stage now compiles the React
# SPA into server/public before compiling the server.

# --- stage 1: build ---
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
COPY server/package.json server/
COPY web/package.json web/
RUN npm ci
COPY server server
COPY web web
RUN npm run build

# --- stage 2: production dependencies only ---
# Web deps are ALL devDependencies, so --omit=dev ships zero frontend tooling.
FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
COPY server/package.json server/
COPY web/package.json web/
RUN npm ci --omit=dev

# --- stage 3: runtime ---
FROM node:22-alpine
ENV NODE_ENV=production
WORKDIR /app
COPY --from=deps  /app/node_modules  ./node_modules
COPY --from=build /app/server/dist   ./server/dist
COPY --from=build /app/server/public ./server/public
COPY server/drizzle ./server/drizzle
USER node
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s CMD wget -qO- http://127.0.0.1:8080/healthz || exit 1
CMD ["node", "server/dist/server.js"]
