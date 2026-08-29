
# Odin 2 Portal DSU -> Virtual DS4 Bridge v2

This version corrects the DSU response offsets against the published
Cemuhook/DSU protocol and uses the official ViGEm DS4_REPORT_EX field layout.

References:
- https://github.com/v1993/cemuhook-protocol
- https://github.com/nefarius/ViGEmClient

## What you need

- Windows 10/11 x64
- Odin 2 Portal and PC on the same LAN
- AndroidDSU running on the Portal
- ViGEmBus 1.22.0 (final official release)
- .NET 8 SDK
- Matching x64 ViGEmClient.dll next to the EXE

## Build

Open PowerShell in this folder:

dotnet build -c Release

The EXE will be in:

bin\Release\net8.0-windows\OdinPortalDsuDs4Bridge.exe

Put the matching x64 `ViGEmClient.dll` beside the EXE.

## Run

Odin 2 Portal: start AndroidDSU.

On Windows:

OdinPortalDsuDs4Bridge.exe 192.168.1.108

Replace the IP with the Portal's IP.

The program should print:

Virtual DualShock 4: READY
Waiting for AndroidDSU packets...

Then, when packets arrive:

DSU OK | gyro ... dps | accel ... g

## First test

Do NOT start the game first.

Run:

joy.cpl

You should see the virtual DualShock 4 / Wireless Controller.

The bridge console must also show changing gyro values while you rotate the Portal.

If the console shows gyro values but the game does not respond to gyro, the
Portal -> DSU -> Windows portion is working and the remaining problem is
game-specific motion/HID compatibility or axis calibration.

## Windows Firewall

If no packets arrive, allow the EXE through Windows Defender Firewall for
Private networks. UDP 26760 is used.

## Important limitation

This is a prototype and has not been tested on your exact Odin 2 Portal.
Do not treat it as a guaranteed finished product.

The DSU protocol's actual controller-data response is 100 bytes and places
accelerometer at payload offsets 56/60/64 and gyro at 68/72/76. Since the
20-byte header/type precedes the payload, the corresponding absolute offsets
used by this program are 76/80/84 and 88/92/96.

ViGEm's documented DS4_REPORT_EX places gyro and accelerometer immediately
after timestamp/battery and exposes `vigem_target_ds4_update_ex()` for full
reports.

## If the bridge starts but reports "ViGEmClient.dll not found"

Put a matching x64 `ViGEmClient.dll` in the same folder as the EXE.

## If it reports "ViGEm connect failed"

ViGEmBus is missing or incompatible. Install the final official ViGEmBus
release and reboot Windows.

## If it reports no DSU packets

Check the Portal IP, same LAN, AndroidDSU running, and Windows Firewall.

The PC is the DSU client and the Portal/AndroidDSU is the DSU server.
Therefore the bridge sends requests TO the Portal's 26760 port.
