# Atmos II Pixel display: USB protocol

Reverse engineered from USBPcap captures of CMAgent (MasterCTRL 2026-11) talking to a
Cooler Master MasterLiquid Atmos II Pixel LED 360.
Only the "ssr" (live stream) path is documented and used. The "flash" path (uploads animations
into device storage) and any firmware/DFU/reset commands are deliberately left alone.

## Device

- USB VID `0x2516`, PID `0x021C`, 3 HID interfaces: MI_00 (vendor, control/status), MI_01 (vendor, display), MI_02 (shows up as a mouse, unused).
- Display: 556 round-layout LEDs on a 32x32 grid, rgb888, max 15 fps.

## Reports

Report type in `wValue` high byte: 2 = Output, 3 = Feature. First data byte is always the report ID.

| ID | Interface | Type | Dir | Data | Meaning |
|----|-----------|------|-----|------|---------|
| 0x02 | 0 | Output | host→dev | `02 04` | sent once at connect ("host here") |
| 0x02 | 0 | Output | host→dev | `02 03` | keepalive, every ~3 s |
| 0x16 | 0 | Output | host→dev | `16 01` | sent at connect and after every settings change (likely "SSR active") |
| 0x01 | 0 | Feature | dev→host | `01 40 16 01 00 00 02` (8 bytes) | generic caps |
| 0x14 | 0 | Feature | dev→host | TLV, 38 bytes | display caps: tag 03=width 32, 04=height 32, 06=maxFps 15, 0a=maxFrameCount 212 (u16 LE) |
| 0x15 | 0 | Feature | dev→host | `15 01` | current display mode (1 = ssr) |
| 0x32 | 0 | Feature | dev→host | `32 2c 02` | LED count, u16 LE = 556 |
| 0x33 | 0 | Feature | both | 8 LED indices in, (index,row,col) out | LED position map query (read-only). Same map as the `lamp-layout` caps in CMAgent.exe, so we don't need it |
| 0x05 | 0 | Feature | dev→host | 17 bytes | status, not decoded |
| 0x80 | 1 | Output (interrupt OUT ep 0x02) | host→dev | 64 bytes | frame data, see below |

## Frame data

A frame is 28 output reports of 64 bytes on interface 1:

```
80 dd  II II  <60 bytes = 20 LEDs, R G B>
```

- `II II` = packet index, big-endian, 0..27.
- Concatenate the 28 payloads (1680 bytes = 560 slots, last 4 unused). LED `n` (1-based) is at byte `(n-1)*3`.
- LED numbers come from the `lamp-layout` array (1024 entries, row-major 32x32, 0 = no LED, serpentine rows). Grid cell `y*32+x` → LED number.
- Plain RGB order, no checksum. CMAgent sends packets ~2 ms apart and the full frame ~8x/s even for a static clock.

## Brightness and rotation

Neither has a device command. CMAgent scales the RGB values itself (brightness 0 = all black) and
rotates the image before mapping it to LEDs. At rotation 0 the layout grid is upright.

## Connect sequence seen (CMAgent startup)

1. Get Feature 0x01 → caps
2. Output `02 04`, then `02 03`
3. Output `16 01`
4. Get Feature 0x14, 0x32, then many 0x33 queries (LED map), 0x05, 0x15
5. Start the frame stream; `02 03` keepalive every ~3 s.

On shutdown CMAgent sends nothing special, it just stops.

## Windows report sizes (HidSharp)

- MI_00: output 2 bytes (report ID + 1 byte), feature 49.
- MI_01: output 65 bytes. It has **no report IDs**, so the buffer is `00` + the 64 wire bytes (`80 dd ...`).

## Without a host

With CMAgent stopped (no frames, no keepalive) the pump loops its built-in startup animation.
Nothing gets stuck, so a crash on our side is harmless.

## Driving it ourselves (verified 2026-11-15)

Connect with `02 04`, `02 03`, `16 01`, then send frames. Colors, LED order, brightness and rotation 90
all showed correctly. A frame stays on screen with only the `02 03` keepalive every 3 s, so frames
only need sending when the image changes (CMAgent's constant ~8 fps stream is unnecessary).

Not tested: whether `02 04` / `16 01` are strictly required (we send them, same as CMAgent).
