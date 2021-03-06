using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using DiscImageChef.CommonTypes;
using DiscImageChef.CommonTypes.Enums;
using DiscImageChef.CommonTypes.Structs;
using DiscImageChef.Core.Logging;
using DiscImageChef.Core.Media.Detection;
using DiscImageChef.Database.Models;
using DiscImageChef.Decoders.CD;
using DiscImageChef.Devices;
using Device = DiscImageChef.Database.Models.Device;

namespace DiscImageChef.Core.Media.Info
{
    public static class CompactDisc
    {
        /// <summary>Gets the offset bytes from a Compact Disc</summary>
        /// <param name="cdOffset">Offset entry from database</param>
        /// <param name="dbDev">Device entry from database</param>
        /// <param name="debug">Debug</param>
        /// <param name="dev">Opened device</param>
        /// <param name="dskType">Detected disk type</param>
        /// <param name="dumpLog">Dump log if applicable</param>
        /// <param name="tracks">Disc track list</param>
        /// <param name="updateStatus">UpdateStatus event</param>
        /// <param name="driveOffset">Drive offset</param>
        /// <param name="combinedOffset">Combined offset</param>
        /// <returns><c>true</c> if offset could be found, <c>false</c> otherwise</returns>
        [SuppressMessage("ReSharper", "TooWideLocalVariableScope")]
        public static void GetOffset(CdOffset cdOffset, Device dbDev, bool debug, DiscImageChef.Devices.Device dev,
                                     MediaType dskType, DumpLog dumpLog, Track[] tracks,
                                     UpdateStatusHandler updateStatus, out int? driveOffset, out int? combinedOffset)
        {
            byte[]     cmdBuf;
            bool       sense;
            int        minute;
            int        second;
            int        frame;
            byte[]     sectorSync;
            byte[]     tmpBuf;
            int        lba;
            int        diff;
            Track      dataTrack   = default;
            Track      audioTrack  = default;
            bool       offsetFound = false;
            const uint sectorSize  = 2352;
            driveOffset    = cdOffset?.Offset * 4;
            combinedOffset = null;

            if(dskType != MediaType.VideoNowColor)
            {
                if(tracks.Any(t => t.TrackType != TrackType.Audio))
                {
                    dataTrack = tracks.FirstOrDefault(t => t.TrackType != TrackType.Audio);

                    if(dataTrack.TrackSequence != 0)
                    {
                        // Build sync
                        sectorSync = new byte[]
                        {
                            0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
                        };

                        tmpBuf = new byte[sectorSync.Length];

                        // Plextor READ CDDA
                        if(dbDev?.ATAPI?.RemovableMedias?.Any(d => d.SupportsPlextorReadCDDA == true) == true ||
                           dbDev?.SCSI?.RemovableMedias?.Any(d => d.SupportsPlextorReadCDDA  == true) == true ||
                           dev.Manufacturer.ToLowerInvariant()                                        == "plextor")
                        {
                            sense = dev.PlextorReadCdDa(out cmdBuf, out _, (uint)dataTrack.TrackStartSector, sectorSize,
                                                        3, PlextorSubchannel.None, dev.Timeout, out _);

                            if(!sense &&
                               !dev.Error)
                            {
                                for(int i = 0; i < cmdBuf.Length - sectorSync.Length; i++)
                                {
                                    Array.Copy(cmdBuf, i, tmpBuf, 0, sectorSync.Length);

                                    if(!tmpBuf.SequenceEqual(sectorSync))
                                        continue;

                                    // De-scramble M and S
                                    minute = cmdBuf[i + 12] ^ 0x01;
                                    second = cmdBuf[i + 13] ^ 0x80;
                                    frame  = cmdBuf[i + 14];

                                    // Convert to binary
                                    minute = ((minute / 16) * 10) + (minute & 0x0F);
                                    second = ((second / 16) * 10) + (second & 0x0F);
                                    frame  = ((frame  / 16) * 10) + (frame  & 0x0F);

                                    // Calculate the first found LBA
                                    lba = ((minute * 60 * 75) + (second * 75) + frame) - 150;

                                    // Calculate the difference between the found LBA and the requested one
                                    diff = (int)dataTrack.TrackStartSector - lba;

                                    combinedOffset = i + (2352 * diff);
                                    offsetFound    = true;

                                    break;
                                }
                            }
                        }

                        if(!offsetFound &&
                           (debug || dbDev?.ATAPI?.RemovableMedias?.Any(d => d.CanReadCdScrambled == true) == true ||
                            dbDev?.SCSI?.RemovableMedias?.Any(d => d.CanReadCdScrambled           == true) == true ||
                            dev.Manufacturer.ToLowerInvariant() ==
                            "hl-dt-st"))
                        {
                            sense = dev.ReadCd(out cmdBuf, out _, (uint)dataTrack.TrackStartSector, sectorSize, 3,
                                               MmcSectorTypes.Cdda, false, false, false, MmcHeaderCodes.None, true,
                                               false, MmcErrorField.None, MmcSubchannel.None, dev.Timeout, out _);

                            if(!sense &&
                               !dev.Error)
                            {
                                for(int i = 0; i < cmdBuf.Length - sectorSync.Length; i++)
                                {
                                    Array.Copy(cmdBuf, i, tmpBuf, 0, sectorSync.Length);

                                    if(!tmpBuf.SequenceEqual(sectorSync))
                                        continue;

                                    // De-scramble M and S
                                    minute = cmdBuf[i + 12] ^ 0x01;
                                    second = cmdBuf[i + 13] ^ 0x80;
                                    frame  = cmdBuf[i + 14];

                                    // Convert to binary
                                    minute = ((minute / 16) * 10) + (minute & 0x0F);
                                    second = ((second / 16) * 10) + (second & 0x0F);
                                    frame  = ((frame  / 16) * 10) + (frame  & 0x0F);

                                    // Calculate the first found LBA
                                    lba = ((minute * 60 * 75) + (second * 75) + frame) - 150;

                                    // Calculate the difference between the found LBA and the requested one
                                    diff = (int)dataTrack.TrackStartSector - lba;

                                    combinedOffset = i + (2352 * diff);
                                    offsetFound    = true;

                                    break;
                                }
                            }
                        }
                    }
                }

                if(offsetFound)
                    return;

                // Try to get another the offset some other way, we need an audio track just after a data track, same session

                for(int i = 1; i < tracks.Length; i++)
                {
                    if(tracks[i - 1].TrackType == TrackType.Audio ||
                       tracks[i].TrackType     != TrackType.Audio)
                        continue;

                    dataTrack  = tracks[i - 1];
                    audioTrack = tracks[i];

                    break;
                }

                if(dataTrack.TrackSequence  == 0 ||
                   audioTrack.TrackSequence == 0)
                    return;

                // Found them
                sense = dev.ReadCd(out cmdBuf, out _, (uint)audioTrack.TrackStartSector, sectorSize, 3,
                                   MmcSectorTypes.Cdda, false, false, false, MmcHeaderCodes.None, true, false,
                                   MmcErrorField.None, MmcSubchannel.None, dev.Timeout, out _);

                if(sense || dev.Error)
                    return;

                dataTrack.TrackEndSector += 150;

                // Calculate MSF
                minute = (int)dataTrack.TrackEndSector                     / 4500;
                second = ((int)dataTrack.TrackEndSector - (minute * 4500)) / 75;
                frame  = (int)dataTrack.TrackEndSector - (minute * 4500) - (second * 75);

                dataTrack.TrackEndSector -= 150;

                // Convert to BCD
                minute = ((minute / 10) << 4) + (minute % 10);
                ;
                second = ((second / 10) << 4) + (second % 10);
                ;
                frame = ((frame / 10) << 4) + (frame % 10);
                ;

                // Scramble M and S
                minute ^= 0x01;
                second ^= 0x80;

                // Build sync
                sectorSync = new byte[]
                {
                    0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, (byte)minute, (byte)second,
                    (byte)frame
                };

                tmpBuf = new byte[sectorSync.Length];

                for(int i = 0; i < cmdBuf.Length - sectorSync.Length; i++)
                {
                    Array.Copy(cmdBuf, i, tmpBuf, 0, sectorSync.Length);

                    if(!tmpBuf.SequenceEqual(sectorSync))
                        continue;

                    combinedOffset = i + 2352;
                    offsetFound    = true;

                    break;
                }

                if(offsetFound || audioTrack.TrackPregap <= 0)
                    return;

                sense = dev.ReadCd(out byte[] dataBuf, out _, (uint)dataTrack.TrackEndSector, sectorSize, 1,
                                   MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true, true,
                                   MmcErrorField.None, MmcSubchannel.None, dev.Timeout, out _);

                if(sense || dev.Error)
                    return;

                for(int i = 0; i < dataBuf.Length; i++)
                    dataBuf[i] ^= Sector.ScrambleTable[i];

                for(int i = 0; i < 2352; i++)
                {
                    byte[] dataSide  = new byte[2352 - i];
                    byte[] audioSide = new byte[2352 - i];

                    Array.Copy(dataBuf, i, dataSide, 0, dataSide.Length);
                    Array.Copy(cmdBuf, 0, audioSide, 0, audioSide.Length);

                    if(!dataSide.SequenceEqual(audioSide))
                        continue;

                    combinedOffset = audioSide.Length;

                    break;
                }
            }
            else
            {
                byte[] videoNowColorFrame = new byte[9 * sectorSize];

                sense = dev.ReadCd(out cmdBuf, out _, 0, sectorSize, 9, MmcSectorTypes.AllTypes, false, false, true,
                                   MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None, MmcSubchannel.None,
                                   dev.Timeout, out _);

                if(sense || dev.Error)
                {
                    sense = dev.ReadCd(out cmdBuf, out _, 0, sectorSize, 9, MmcSectorTypes.Cdda, false, false, true,
                                       MmcHeaderCodes.None, true, true, MmcErrorField.None, MmcSubchannel.None,
                                       dev.Timeout, out _);

                    if(sense || dev.Error)
                    {
                        videoNowColorFrame = null;
                    }
                }

                if(videoNowColorFrame is null)
                {
                    dumpLog?.WriteLine("Could not find VideoNow Color frame offset, dump may not be correct.");
                    updateStatus?.Invoke("Could not find VideoNow Color frame offset, dump may not be correct.");
                }
                else
                {
                    combinedOffset = MMC.GetVideoNowColorOffset(videoNowColorFrame);
                    dumpLog?.WriteLine($"VideoNow Color frame is offset {combinedOffset} bytes.");
                    updateStatus?.Invoke($"VideoNow Color frame is offset {combinedOffset} bytes.");
                }
            }
        }
    }
}