# CCNA Lab 02 — Dissecting the Radmin tunnel with Wireshark

**Goal:** see with your own eyes that one ping exists at two layers — plaintext
inside the virtual network, encrypted UDP outside it.

## Setup
Wireshark (wireshark.org), your PC + one friend online on the Radmin network.

## Part A — inside the tunnel
1. Capture on the **Radmin VPN adapter**. Filter: `icmp`.
2. `ping 26.x.x.x` (your friend's Radmin IP).
3. Observe: plain ICMP echo/reply between two 26.x addresses, as if you shared
   a switch. Note the TTL and payload — nothing is hidden here.

## Part B — outside the tunnel (same ping!)
1. Capture on your **physical adapter** (Wi-Fi/Ethernet). Filter: `udp`.
2. Ping again. Find the UDP stream to a public IP that pulses in step with
   your pings — Radmin's encapsulation.
3. Observe: you cannot see ICMP, addresses, or payload — only encrypted UDP
   between your NAT'd address and either your friend's public IP (direct P2P)
   or a Radmin relay (CGNAT fallback, SDD §18.3). Compare packet sizes A vs B:
   the difference is the encapsulation overhead — the MTU story of SDD §18.4.

## Write down
- Radmin adapter's IP/subnet (`ipconfig /all`), the outer UDP endpoints,
  inner vs outer packet sizes, and: direct P2P or relay? How do you know?
