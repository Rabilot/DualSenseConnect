# Third-party notices

DualSenseConnect uses the following libraries. Their licenses apply to the parts they cover.

| Library | Use | License |
|---|---|---|
| [HidSharp](https://www.zer7.com/software/hidsharp) by James F. Bellinger | Reading and writing the controller's HID reports | Apache License 2.0 |
| [NAudio](https://github.com/naudio/NAudio) (NAudio.Core, NAudio.Wasapi) by Mark Heath | WASAPI loopback capture, audio device notifications | MIT |
| [Concentus](https://github.com/lostromb/concentus) by Logan Stromberg | Opus encoding of the sound sent to the controller (a C# port of libopus) | BSD-style (Opus license) |

## Protocol references

The Bluetooth audio, haptics and headphone-detection details were implemented from the
descriptions in these projects. No code was copied from them.

- [vingt-2/dualsense-companion](https://github.com/vingt-2/dualsense-companion) (MIT)
- [ebraminio/LinuxAudio4Dualsense5](https://github.com/ebraminio/LinuxAudio4Dualsense5) (AGPL-3.0)
- The Linux kernel `hid-playstation` driver (GPL-2.0), for the input and output report layouts

## Optional companion software

[VB-CABLE](https://vb-audio.com/Cable/) by VB-Audio Software is not included. It is donationware;
users download and install it themselves from www.vb-cable.com.
