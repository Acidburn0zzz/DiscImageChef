﻿// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : XGD.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Core algorithms.
//
// --[ Description ] ----------------------------------------------------------
//
//     Dumps Xbox Game Discs.
//
// --[ License ] --------------------------------------------------------------
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as
//     published by the Free Software Foundation, either version 3 of the
//     License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License
//     along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2020 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using DiscImageChef.CommonTypes;
using DiscImageChef.CommonTypes.Enums;
using DiscImageChef.CommonTypes.Extents;
using DiscImageChef.CommonTypes.Interfaces;
using DiscImageChef.CommonTypes.Interop;
using DiscImageChef.CommonTypes.Structs;
using DiscImageChef.Console;
using DiscImageChef.Core.Logging;
using DiscImageChef.Decoders.DVD;
using DiscImageChef.Decoders.SCSI;
using DiscImageChef.Decoders.Xbox;
using DiscImageChef.Devices;
using Schemas;
using PlatformID = DiscImageChef.CommonTypes.Interop.PlatformID;
using TrackType = DiscImageChef.CommonTypes.Enums.TrackType;

namespace DiscImageChef.Core.Devices.Dumping
{
    /// <summary>Implements dumping an Xbox Game Disc using a Kreon drive</summary>
    partial class Dump
    {
        /// <summary>Dumps an Xbox Game Disc using a Kreon drive</summary>
        /// <param name="mediaTags">Media tags as retrieved in MMC layer</param>
        /// <param name="dskType">Disc type as detected in MMC layer</param>
        internal void Xgd(Dictionary<MediaTagType, byte[]> mediaTags, ref MediaType dskType)
        {
            bool       sense;
            const uint BLOCK_SIZE   = 2048;
            uint       blocksToRead = 64;
            DateTime   start;
            DateTime   end;
            double     totalDuration = 0;
            double     currentSpeed  = 0;
            double     maxSpeed      = double.MinValue;
            double     minSpeed      = double.MaxValue;

            if(DetectOS.GetRealPlatformID() != PlatformID.Win32NT)
            {
                bool isAdmin = _dev.IsRemote ? _dev.IsRemoteAdmin : DetectOS.IsAdmin;

                if(!isAdmin)
                {
                    DicConsole.
                        ErrorWriteLine("Because of the commands sent to a device, dumping XGD must be done with administrative privileges. Cannot continue.");

                    _dumpLog.WriteLine("Cannot dump XGD without administrative privileges.");

                    return;
                }
            }

            if(mediaTags.ContainsKey(MediaTagType.DVD_PFI))
                mediaTags.Remove(MediaTagType.DVD_PFI);

            if(mediaTags.ContainsKey(MediaTagType.DVD_DMI))
                mediaTags.Remove(MediaTagType.DVD_DMI);

            // Drive shall move to lock state when a new disc is inserted. Old kreon versions do not lock correctly so save this
            sense = _dev.ReadCapacity(out byte[] coldReadCapacity, out byte[] senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot get disc capacity.");
                StoppingErrorMessage?.Invoke("Cannot get disc capacity.");

                return;
            }

            // Drive shall move to lock state when a new disc is inserted. Old kreon versions do not lock correctly so save this
            sense = _dev.ReadDiscStructure(out byte[] coldPfi, out senseBuf, MmcDiscStructureMediaType.Dvd, 0, 0,
                                           MmcDiscStructureFormat.PhysicalInformation, 0, 0, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot get PFI.");
                StoppingErrorMessage?.Invoke("Cannot get PFI.");

                return;
            }

            UpdateStatus?.Invoke("Reading Xbox Security Sector.");
            _dumpLog.WriteLine("Reading Xbox Security Sector.");
            sense = _dev.KreonExtractSs(out byte[] ssBuf, out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot get Xbox Security Sector, not continuing.");
                StoppingErrorMessage?.Invoke("Cannot get Xbox Security Sector, not continuing.");

                return;
            }

            _dumpLog.WriteLine("Decoding Xbox Security Sector.");
            UpdateStatus?.Invoke("Decoding Xbox Security Sector.");
            SS.SecuritySector? xboxSs = SS.Decode(ssBuf);

            if(!xboxSs.HasValue)
            {
                _dumpLog.WriteLine("Cannot decode Xbox Security Sector, not continuing.");
                StoppingErrorMessage?.Invoke("Cannot decode Xbox Security Sector, not continuing.");

                return;
            }

            byte[] tmpBuf = new byte[ssBuf.Length - 4];
            Array.Copy(ssBuf, 4, tmpBuf, 0, ssBuf.Length - 4);
            mediaTags.Add(MediaTagType.Xbox_SecuritySector, tmpBuf);

            // Get video partition size
            DicConsole.DebugWriteLine("Dump-media command", "Getting video partition size");
            UpdateStatus?.Invoke("Locking drive.");
            _dumpLog.WriteLine("Locking drive.");
            sense = _dev.KreonLock(out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot lock drive, not continuing.");
                StoppingErrorMessage?.Invoke("Cannot lock drive, not continuing.");

                return;
            }

            UpdateStatus?.Invoke("Getting video partition size.");
            _dumpLog.WriteLine("Getting video partition size.");
            sense = _dev.ReadCapacity(out byte[] readBuffer, out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot get disc capacity.");
                StoppingErrorMessage?.Invoke("Cannot get disc capacity.");

                return;
            }

            ulong totalSize =
                (ulong)((readBuffer[0] << 24) + (readBuffer[1] << 16) + (readBuffer[2] << 8) + readBuffer[3]);

            UpdateStatus?.Invoke("Reading Physical Format Information.");
            _dumpLog.WriteLine("Reading Physical Format Information.");

            sense = _dev.ReadDiscStructure(out readBuffer, out senseBuf, MmcDiscStructureMediaType.Dvd, 0, 0,
                                           MmcDiscStructureFormat.PhysicalInformation, 0, 0, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot get PFI.");
                StoppingErrorMessage?.Invoke("Cannot get PFI.");

                return;
            }

            tmpBuf = new byte[readBuffer.Length - 4];
            Array.Copy(readBuffer, 4, tmpBuf, 0, readBuffer.Length - 4);
            mediaTags.Add(MediaTagType.DVD_PFI, tmpBuf);
            DicConsole.DebugWriteLine("Dump-media command", "Video partition total size: {0} sectors", totalSize);

            ulong l0Video =
                (PFI.Decode(readBuffer).Value.Layer0EndPSN - PFI.Decode(readBuffer).Value.DataAreaStartPSN) + 1;

            ulong l1Video = (totalSize - l0Video) + 1;
            UpdateStatus?.Invoke("Reading Disc Manufacturing Information.");
            _dumpLog.WriteLine("Reading Disc Manufacturing Information.");

            sense = _dev.ReadDiscStructure(out readBuffer, out senseBuf, MmcDiscStructureMediaType.Dvd, 0, 0,
                                           MmcDiscStructureFormat.DiscManufacturingInformation, 0, 0, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot get DMI.");
                StoppingErrorMessage?.Invoke("Cannot get DMI.");

                return;
            }

            tmpBuf = new byte[readBuffer.Length - 4];
            Array.Copy(readBuffer, 4, tmpBuf, 0, readBuffer.Length - 4);
            mediaTags.Add(MediaTagType.DVD_DMI, tmpBuf);

            // Should be a safe value to detect the lock command was ignored, and we're indeed getting the whole size and not the locked one
            if(totalSize > 300000)
            {
                UpdateStatus?.Invoke("Video partition is too big, did lock work? Trying cold values.");
                _dumpLog.WriteLine("Video partition is too big, did lock work? Trying cold values.");

                totalSize = (ulong)((coldReadCapacity[0] << 24) + (coldReadCapacity[1] << 16) +
                                    (coldReadCapacity[2] << 8)  + coldReadCapacity[3]);

                tmpBuf = new byte[coldPfi.Length - 4];
                Array.Copy(coldPfi, 4, tmpBuf, 0, coldPfi.Length - 4);
                mediaTags.Remove(MediaTagType.DVD_PFI);
                mediaTags.Add(MediaTagType.DVD_PFI, tmpBuf);
                DicConsole.DebugWriteLine("Dump-media command", "Video partition total size: {0} sectors", totalSize);

                l0Video = (PFI.Decode(coldPfi).Value.Layer0EndPSN - PFI.Decode(coldPfi).Value.DataAreaStartPSN) + 1;

                l1Video = (totalSize - l0Video) + 1;

                if(totalSize > 300000)
                {
                    _dumpLog.WriteLine("Cannot get video partition size, not continuing. Try to eject and reinsert the drive, if it keeps happening, contact support.");

                    StoppingErrorMessage?.
                        Invoke("Cannot get video partition size, not continuing. Try to eject and reinsert the drive, if it keeps happening, contact support.");

                    return;
                }
            }

            // Get game partition size
            DicConsole.DebugWriteLine("Dump-media command", "Getting game partition size");
            UpdateStatus?.Invoke("Unlocking drive (Xtreme).");
            _dumpLog.WriteLine("Unlocking drive (Xtreme).");
            sense = _dev.KreonUnlockXtreme(out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot unlock drive, not continuing.");
                StoppingErrorMessage?.Invoke("Cannot unlock drive, not continuing.");

                return;
            }

            UpdateStatus?.Invoke("Getting game partition size.");
            _dumpLog.WriteLine("Getting game partition size.");
            sense = _dev.ReadCapacity(out readBuffer, out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot get disc capacity.");
                StoppingErrorMessage?.Invoke("Cannot get disc capacity.");

                return;
            }

            ulong gameSize =
                (ulong)((readBuffer[0] << 24) + (readBuffer[1] << 16) + (readBuffer[2] << 8) + readBuffer[3]) + 1;

            DicConsole.DebugWriteLine("Dump-media command", "Game partition total size: {0} sectors", gameSize);

            // Get middle zone size
            DicConsole.DebugWriteLine("Dump-media command", "Getting middle zone size");
            UpdateStatus?.Invoke("Unlocking drive (Wxripper).");
            _dumpLog.WriteLine("Unlocking drive (Wxripper).");
            sense = _dev.KreonUnlockWxripper(out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot unlock drive, not continuing.");
                StoppingErrorMessage?.Invoke("Cannot unlock drive, not continuing.");

                return;
            }

            UpdateStatus?.Invoke("Getting disc size.");
            _dumpLog.WriteLine("Getting disc size.");
            sense = _dev.ReadCapacity(out readBuffer, out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot get disc capacity.");
                StoppingErrorMessage?.Invoke("Cannot get disc capacity.");

                return;
            }

            totalSize = (ulong)((readBuffer[0] << 24) + (readBuffer[1] << 16) + (readBuffer[2] << 8) + readBuffer[3]);
            UpdateStatus?.Invoke("Reading Physical Format Information.");
            _dumpLog.WriteLine("Reading Physical Format Information.");

            sense = _dev.ReadDiscStructure(out readBuffer, out senseBuf, MmcDiscStructureMediaType.Dvd, 0, 0,
                                           MmcDiscStructureFormat.PhysicalInformation, 0, 0, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot get PFI.");
                StoppingErrorMessage?.Invoke("Cannot get PFI.");

                return;
            }

            DicConsole.DebugWriteLine("Dump-media command", "Unlocked total size: {0} sectors", totalSize);
            ulong                         blocks      = totalSize + 1;
            PFI.PhysicalFormatInformation wxRipperPfi = PFI.Decode(readBuffer).Value;

            UpdateStatus?.Invoke($"WxRipper PFI's Data Area Start PSN: {wxRipperPfi.DataAreaStartPSN} sectors");
            UpdateStatus?.Invoke($"WxRipper PFI's Layer 0 End PSN: {wxRipperPfi.Layer0EndPSN} sectors");
            _dumpLog.WriteLine($"WxRipper PFI's Data Area Start PSN: {wxRipperPfi.DataAreaStartPSN} sectors");
            _dumpLog.WriteLine($"WxRipper PFI's Layer 0 End PSN: {wxRipperPfi.Layer0EndPSN} sectors");

            ulong middleZone =
                (totalSize - ((wxRipperPfi.Layer0EndPSN - wxRipperPfi.DataAreaStartPSN) + 1) - gameSize) + 1;

            tmpBuf = new byte[readBuffer.Length - 4];
            Array.Copy(readBuffer, 4, tmpBuf, 0, readBuffer.Length - 4);
            mediaTags.Add(MediaTagType.Xbox_PFI, tmpBuf);

            UpdateStatus?.Invoke("Reading Disc Manufacturing Information.");
            _dumpLog.WriteLine("Reading Disc Manufacturing Information.");

            sense = _dev.ReadDiscStructure(out readBuffer, out senseBuf, MmcDiscStructureMediaType.Dvd, 0, 0,
                                           MmcDiscStructureFormat.DiscManufacturingInformation, 0, 0, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot get DMI.");
                StoppingErrorMessage?.Invoke("Cannot get DMI.");

                return;
            }

            tmpBuf = new byte[readBuffer.Length - 4];
            Array.Copy(readBuffer, 4, tmpBuf, 0, readBuffer.Length - 4);
            mediaTags.Add(MediaTagType.Xbox_DMI, tmpBuf);

            totalSize = l0Video + l1Video + (middleZone * 2) + gameSize;
            ulong layerBreak = l0Video + middleZone + (gameSize / 2);

            UpdateStatus?.Invoke($"Video layer 0 size: {l0Video} sectors");
            UpdateStatus?.Invoke($"Video layer 1 size: {l1Video} sectors");
            UpdateStatus?.Invoke($"Middle zone size: {middleZone} sectors");
            UpdateStatus?.Invoke($"Game data size: {gameSize} sectors");
            UpdateStatus?.Invoke($"Total size: {totalSize} sectors");
            UpdateStatus?.Invoke($"Real layer break: {layerBreak}");
            UpdateStatus?.Invoke("");

            _dumpLog.WriteLine("Video layer 0 size: {0} sectors", l0Video);
            _dumpLog.WriteLine("Video layer 1 size: {0} sectors", l1Video);
            _dumpLog.WriteLine("Middle zone 0 size: {0} sectors", middleZone);
            _dumpLog.WriteLine("Game data 0 size: {0} sectors", gameSize);
            _dumpLog.WriteLine("Total 0 size: {0} sectors", totalSize);
            _dumpLog.WriteLine("Real layer break: {0}", layerBreak);

            bool read12 = !_dev.Read12(out readBuffer, out senseBuf, 0, false, true, false, false, 0, BLOCK_SIZE, 0, 1,
                                       false, _dev.Timeout, out _);

            if(!read12)
            {
                _dumpLog.WriteLine("Cannot read medium, aborting scan...");
                StoppingErrorMessage?.Invoke("Cannot read medium, aborting scan...");

                return;
            }

            _dumpLog.WriteLine("Using SCSI READ (12) command.");
            UpdateStatus?.Invoke("Using SCSI READ (12) command.");

            // Set speed
            if(_speedMultiplier >= 0)
            {
                _dumpLog.WriteLine($"Setting speed to {_speed}x.");
                UpdateStatus?.Invoke($"Setting speed to {_speed}x.");

                _speed *= _speedMultiplier;

                if(_speed == 0 ||
                   _speed > 0xFFFF)
                    _speed = 0xFFFF;

                _dev.SetCdSpeed(out _, RotationalControl.ClvAndImpureCav, (ushort)_speed, 0, _dev.Timeout, out _);
            }

            while(true)
            {
                if(read12)
                {
                    sense = _dev.Read12(out readBuffer, out senseBuf, 0, false, false, false, false, 0, BLOCK_SIZE, 0,
                                        blocksToRead, false, _dev.Timeout, out _);

                    if(sense || _dev.Error)
                        blocksToRead /= 2;
                }

                if(!_dev.Error ||
                   blocksToRead == 1)
                    break;
            }

            if(_dev.Error)
            {
                _dumpLog.WriteLine("Device error {0} trying to guess ideal transfer length.", _dev.LastError);
                StoppingErrorMessage?.Invoke($"Device error {_dev.LastError} trying to guess ideal transfer length.");

                return;
            }

            if(_skip < blocksToRead)
                _skip = blocksToRead;

            bool ret = true;

            foreach(MediaTagType tag in mediaTags.Keys)
            {
                if(_outputPlugin.SupportedMediaTags.Contains(tag))
                    continue;

                ret = false;
                _dumpLog.WriteLine($"Output format does not support {tag}.");
                ErrorMessage?.Invoke($"Output format does not support {tag}.");
            }

            if(!ret)
            {
                if(_force)
                {
                    _dumpLog.WriteLine("Several media tags not supported, continuing...");
                    ErrorMessage?.Invoke("Several media tags not supported, continuing...");
                }
                else
                {
                    _dumpLog.WriteLine("Several media tags not supported, not continuing...");
                    StoppingErrorMessage?.Invoke("Several media tags not supported, not continuing...");

                    return;
                }
            }

            _dumpLog.WriteLine("Reading {0} sectors at a time.", blocksToRead);
            UpdateStatus?.Invoke($"Reading {blocksToRead} sectors at a time.");

            var mhddLog = new MhddLog(_outputPrefix + ".mhddlog.bin", _dev, blocks, BLOCK_SIZE, blocksToRead);
            var ibgLog  = new IbgLog(_outputPrefix  + ".ibg", 0x0010);
            ret = _outputPlugin.Create(_outputPath, dskType, _formatOptions, blocks, BLOCK_SIZE);

            // Cannot create image
            if(!ret)
            {
                _dumpLog.WriteLine("Error creating output image, not continuing.");
                _dumpLog.WriteLine(_outputPlugin.ErrorMessage);

                StoppingErrorMessage?.Invoke("Error creating output image, not continuing." + Environment.NewLine +
                                             _outputPlugin.ErrorMessage);

                return;
            }

            start = DateTime.UtcNow;
            double imageWriteDuration = 0;

            double           cmdDuration      = 0;
            uint             saveBlocksToRead = blocksToRead;
            DumpHardwareType currentTry       = null;
            ExtentsULong     extents          = null;

            ResumeSupport.Process(true, true, totalSize, _dev.Manufacturer, _dev.Model, _dev.Serial, _dev.PlatformId,
                                  ref _resume, ref currentTry, ref extents);

            if(currentTry == null ||
               extents    == null)
                StoppingErrorMessage?.Invoke("Could not process resume file, not continuing...");

            (_outputPlugin as IWritableOpticalImage).SetTracks(new List<Track>
            {
                new Track
                {
                    TrackBytesPerSector    = (int)BLOCK_SIZE, TrackEndSector      = blocks - 1, TrackSequence = 1,
                    TrackRawBytesPerSector = (int)BLOCK_SIZE, TrackSubchannelType = TrackSubchannelType.None,
                    TrackSession           = 1, TrackType                         = TrackType.Data
                }
            });

            ulong currentSector = _resume.NextBlock;

            if(_resume.NextBlock > 0)
            {
                UpdateStatus?.Invoke($"Resuming from block {_resume.NextBlock}.");
                _dumpLog.WriteLine("Resuming from block {0}.", _resume.NextBlock);
            }

            bool newTrim = false;

            _dumpLog.WriteLine("Reading game partition.");
            UpdateStatus?.Invoke("Reading game partition.");
            DateTime timeSpeedStart   = DateTime.UtcNow;
            ulong    sectorSpeedStart = 0;
            InitProgress?.Invoke();

            for(int e = 0; e <= 16; e++)
            {
                if(_aborted)
                {
                    _resume.NextBlock  = currentSector;
                    currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                    UpdateStatus?.Invoke("Aborted!");
                    _dumpLog.WriteLine("Aborted!");

                    break;
                }

                if(currentSector >= blocks)
                    break;

                ulong extentStart, extentEnd;

                // Extents
                if(e < 16)
                {
                    if(xboxSs.Value.Extents[e].StartPSN <= xboxSs.Value.Layer0EndPSN)
                        extentStart = xboxSs.Value.Extents[e].StartPSN - 0x30000;
                    else
                        extentStart = ((xboxSs.Value.Layer0EndPSN + 1) * 2)               -
                                      ((xboxSs.Value.Extents[e].StartPSN ^ 0xFFFFFF) + 1) - 0x30000;

                    if(xboxSs.Value.Extents[e].EndPSN <= xboxSs.Value.Layer0EndPSN)
                        extentEnd = xboxSs.Value.Extents[e].EndPSN - 0x30000;
                    else
                        extentEnd = ((xboxSs.Value.Layer0EndPSN + 1) * 2)             -
                                    ((xboxSs.Value.Extents[e].EndPSN ^ 0xFFFFFF) + 1) - 0x30000;
                }

                // After last extent
                else
                {
                    extentStart = blocks;
                    extentEnd   = blocks;
                }

                if(currentSector > extentEnd)
                    continue;

                for(ulong i = currentSector; i < extentStart; i += blocksToRead)
                {
                    saveBlocksToRead = blocksToRead;

                    if(_aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        UpdateStatus?.Invoke("Aborted!");
                        _dumpLog.WriteLine("Aborted!");

                        break;
                    }

                    if(extentStart - i < blocksToRead)
                        blocksToRead = (uint)(extentStart - i);

                    #pragma warning disable RECS0018 // Comparison of floating point numbers with equality operator
                    if(currentSpeed > maxSpeed &&
                       currentSpeed != 0)
                        maxSpeed = currentSpeed;

                    if(currentSpeed < minSpeed &&
                       currentSpeed != 0)
                        minSpeed = currentSpeed;
                    #pragma warning restore RECS0018 // Comparison of floating point numbers with equality operator

                    UpdateProgress?.Invoke($"Reading sector {i} of {totalSize} ({currentSpeed:F3} MiB/sec.)", (long)i,
                                           (long)totalSize);

                    sense = _dev.Read12(out readBuffer, out senseBuf, 0, false, false, false, false, (uint)i,
                                        BLOCK_SIZE, 0, blocksToRead, false, _dev.Timeout, out cmdDuration);

                    totalDuration += cmdDuration;

                    if(!sense &&
                       !_dev.Error)
                    {
                        mhddLog.Write(i, cmdDuration);
                        ibgLog.Write(i, currentSpeed * 1024);
                        DateTime writeStart = DateTime.Now;
                        _outputPlugin.WriteSectors(readBuffer, i, blocksToRead);
                        imageWriteDuration += (DateTime.Now - writeStart).TotalSeconds;
                        extents.Add(i, blocksToRead, true);
                    }
                    else
                    {
                        // TODO: Reset device after X errors
                        if(_stopOnError)
                            return; // TODO: Return more cleanly

                        if(i + _skip > blocks)
                            _skip = (uint)(blocks - i);

                        // Write empty data
                        DateTime writeStart = DateTime.Now;
                        _outputPlugin.WriteSectors(new byte[BLOCK_SIZE * _skip], i, _skip);
                        imageWriteDuration += (DateTime.Now - writeStart).TotalSeconds;

                        for(ulong b = i; b < i + _skip; b++)
                            _resume.BadBlocks.Add(b);

                        DicConsole.DebugWriteLine("Dump-Media", "READ error:\n{0}", Sense.PrettifySense(senseBuf));
                        mhddLog.Write(i, cmdDuration < 500 ? 65535 : cmdDuration);

                        ibgLog.Write(i, 0);

                        _dumpLog.WriteLine("Skipping {0} blocks from errored block {1}.", _skip, i);
                        i += _skip - blocksToRead;

                        string[] senseLines = Sense.PrettifySense(senseBuf).Split(new[]
                        {
                            Environment.NewLine
                        }, StringSplitOptions.RemoveEmptyEntries);

                        foreach(string senseLine in senseLines)
                            _dumpLog.WriteLine(senseLine);

                        newTrim = true;
                    }

                    blocksToRead      =  saveBlocksToRead;
                    currentSector     =  i + 1;
                    _resume.NextBlock =  currentSector;
                    sectorSpeedStart  += blocksToRead;

                    double elapsed = (DateTime.UtcNow - timeSpeedStart).TotalSeconds;

                    if(elapsed < 1)
                        continue;

                    currentSpeed     = (sectorSpeedStart * BLOCK_SIZE) / (1048576 * elapsed);
                    sectorSpeedStart = 0;
                    timeSpeedStart   = DateTime.UtcNow;
                }

                for(ulong i = extentStart; i <= extentEnd; i += blocksToRead)
                {
                    saveBlocksToRead = blocksToRead;

                    if(_aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        UpdateStatus?.Invoke("Aborted!");
                        _dumpLog.WriteLine("Aborted!");

                        break;
                    }

                    if(extentEnd - i < blocksToRead)
                        blocksToRead = (uint)(extentEnd - i) + 1;

                    mhddLog.Write(i, cmdDuration);
                    ibgLog.Write(i, currentSpeed * 1024);

                    // Write empty data
                    DateTime writeStart = DateTime.Now;
                    _outputPlugin.WriteSectors(new byte[BLOCK_SIZE * blocksToRead], i, blocksToRead);
                    imageWriteDuration += (DateTime.Now - writeStart).TotalSeconds;
                    blocksToRead       =  saveBlocksToRead;
                    extents.Add(i, blocksToRead, true);
                    currentSector     = i + 1;
                    _resume.NextBlock = currentSector;
                }

                if(!_aborted)
                    currentSector = extentEnd + 1;
            }

            EndProgress?.Invoke();

            // Middle Zone D
            UpdateStatus?.Invoke("Writing Middle Zone D (empty).");
            _dumpLog.WriteLine("Writing Middle Zone D (empty).");
            InitProgress?.Invoke();

            for(ulong middle = currentSector - blocks - 1; middle < middleZone - 1; middle += blocksToRead)
            {
                if(_aborted)
                {
                    currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                    UpdateStatus?.Invoke("Aborted!");
                    _dumpLog.WriteLine("Aborted!");

                    break;
                }

                if(middleZone - 1 - middle < blocksToRead)
                    blocksToRead = (uint)(middleZone - 1 - middle);

                UpdateProgress?.
                    Invoke($"Reading sector {middle + currentSector} of {totalSize} ({currentSpeed:F3} MiB/sec.)",
                           (long)(middle            + currentSector), (long)totalSize);

                mhddLog.Write(middle + currentSector, cmdDuration);
                ibgLog.Write(middle  + currentSector, currentSpeed * 1024);

                // Write empty data
                DateTime writeStart = DateTime.Now;
                _outputPlugin.WriteSectors(new byte[BLOCK_SIZE * blocksToRead], middle + currentSector, blocksToRead);
                imageWriteDuration += (DateTime.Now                                    - writeStart).TotalSeconds;
                extents.Add(currentSector, blocksToRead, true);

                currentSector     += blocksToRead;
                _resume.NextBlock =  currentSector;
            }

            EndProgress?.Invoke();

            blocksToRead = saveBlocksToRead;

            UpdateStatus?.Invoke("Locking drive.");
            _dumpLog.WriteLine("Locking drive.");
            sense = _dev.KreonLock(out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot lock drive, not continuing.");
                StoppingErrorMessage?.Invoke("Cannot lock drive, not continuing.");

                return;
            }

            sense = _dev.ReadCapacity(out readBuffer, out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                StoppingErrorMessage?.Invoke("Cannot get disc capacity.");

                return;
            }

            // Video Layer 1
            _dumpLog.WriteLine("Reading Video Layer 1.");
            UpdateStatus?.Invoke("Reading Video Layer 1.");
            InitProgress?.Invoke();

            for(ulong l1 = (currentSector - blocks - middleZone) + l0Video; l1 < l0Video + l1Video; l1 += blocksToRead)
            {
                if(_aborted)
                {
                    currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                    UpdateStatus?.Invoke("Aborted!");
                    _dumpLog.WriteLine("Aborted!");

                    break;
                }

                if((l0Video + l1Video) - l1 < blocksToRead)
                    blocksToRead = (uint)((l0Video + l1Video) - l1);

                #pragma warning disable RECS0018 // Comparison of floating point numbers with equality operator
                if(currentSpeed > maxSpeed &&
                   currentSpeed != 0)
                    maxSpeed = currentSpeed;

                if(currentSpeed < minSpeed &&
                   currentSpeed != 0)
                    minSpeed = currentSpeed;
                #pragma warning restore RECS0018 // Comparison of floating point numbers with equality operator

                UpdateProgress?.Invoke($"Reading sector {currentSector} of {totalSize} ({currentSpeed:F3} MiB/sec.)",
                                       (long)currentSector, (long)totalSize);

                sense = _dev.Read12(out readBuffer, out senseBuf, 0, false, false, false, false, (uint)l1, BLOCK_SIZE,
                                    0, blocksToRead, false, _dev.Timeout, out cmdDuration);

                totalDuration += cmdDuration;

                if(!sense &&
                   !_dev.Error)
                {
                    mhddLog.Write(currentSector, cmdDuration);
                    ibgLog.Write(currentSector, currentSpeed * 1024);
                    DateTime writeStart = DateTime.Now;
                    _outputPlugin.WriteSectors(readBuffer, currentSector, blocksToRead);
                    imageWriteDuration += (DateTime.Now - writeStart).TotalSeconds;
                    extents.Add(currentSector, blocksToRead, true);
                }
                else
                {
                    // TODO: Reset device after X errors
                    if(_stopOnError)
                        return; // TODO: Return more cleanly

                    // Write empty data
                    DateTime writeStart = DateTime.Now;
                    _outputPlugin.WriteSectors(new byte[BLOCK_SIZE * _skip], currentSector, _skip);
                    imageWriteDuration += (DateTime.Now - writeStart).TotalSeconds;

                    // TODO: Handle errors in video partition
                    //errored += blocksToRead;
                    //resume.BadBlocks.Add(l1);
                    DicConsole.DebugWriteLine("Dump-Media", "READ error:\n{0}", Sense.PrettifySense(senseBuf));
                    mhddLog.Write(l1, cmdDuration < 500 ? 65535 : cmdDuration);

                    ibgLog.Write(l1, 0);
                    _dumpLog.WriteLine("Skipping {0} blocks from errored block {1}.", _skip, l1);
                    l1 += _skip - blocksToRead;

                    string[] senseLines = Sense.PrettifySense(senseBuf).Split(new[]
                    {
                        Environment.NewLine
                    }, StringSplitOptions.RemoveEmptyEntries);

                    foreach(string senseLine in senseLines)
                        _dumpLog.WriteLine(senseLine);
                }

                currentSector     += blocksToRead;
                _resume.NextBlock =  currentSector;
                sectorSpeedStart  += blocksToRead;

                double elapsed = (DateTime.UtcNow - timeSpeedStart).TotalSeconds;

                if(elapsed < 1)
                    continue;

                currentSpeed     = (sectorSpeedStart * BLOCK_SIZE) / (1048576 * elapsed);
                sectorSpeedStart = 0;
                timeSpeedStart   = DateTime.UtcNow;
            }

            EndProgress?.Invoke();

            UpdateStatus?.Invoke("Unlocking drive (Wxripper).");
            _dumpLog.WriteLine("Unlocking drive (Wxripper).");
            sense = _dev.KreonUnlockWxripper(out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Cannot unlock drive, not continuing.");
                StoppingErrorMessage?.Invoke("Cannot unlock drive, not continuing.");

                return;
            }

            sense = _dev.ReadCapacity(out readBuffer, out senseBuf, _dev.Timeout, out _);

            if(sense)
            {
                StoppingErrorMessage?.Invoke("Cannot get disc capacity.");

                return;
            }

            end = DateTime.UtcNow;
            DicConsole.WriteLine();
            mhddLog.Close();

            ibgLog.Close(_dev, blocks, BLOCK_SIZE, (end - start).TotalSeconds, currentSpeed * 1024,
                         (BLOCK_SIZE * (double)(blocks + 1)) / 1024                         / (totalDuration / 1000),
                         _devicePath);

            UpdateStatus?.Invoke($"Dump finished in {(end - start).TotalSeconds} seconds.");

            UpdateStatus?.
                Invoke($"Average dump speed {((double)BLOCK_SIZE * (double)(blocks + 1)) / 1024 / (totalDuration / 1000):F3} KiB/sec.");

            UpdateStatus?.
                Invoke($"Average write speed {((double)BLOCK_SIZE * (double)(blocks + 1)) / 1024 / imageWriteDuration:F3} KiB/sec.");

            _dumpLog.WriteLine("Dump finished in {0} seconds.", (end - start).TotalSeconds);

            _dumpLog.WriteLine("Average dump speed {0:F3} KiB/sec.",
                               ((double)BLOCK_SIZE * (double)(blocks + 1)) / 1024 / (totalDuration / 1000));

            _dumpLog.WriteLine("Average write speed {0:F3} KiB/sec.",
                               ((double)BLOCK_SIZE * (double)(blocks + 1)) / 1024 / imageWriteDuration);

            #region Trimming
            if(_resume.BadBlocks.Count > 0 &&
               !_aborted                   &&
               _trim                       &&
               newTrim)
            {
                start = DateTime.UtcNow;
                UpdateStatus?.Invoke("Trimming bad sectors");
                _dumpLog.WriteLine("Trimming bad sectors");

                ulong[] tmpArray = _resume.BadBlocks.ToArray();
                InitProgress?.Invoke();

                foreach(ulong badSector in tmpArray)
                {
                    if(_aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        _dumpLog.WriteLine("Aborted!");

                        break;
                    }

                    PulseProgress?.Invoke($"Trimming sector {badSector}");

                    sense = _dev.Read12(out readBuffer, out senseBuf, 0, false, false, false, false, (uint)badSector,
                                        BLOCK_SIZE, 0, 1, false, _dev.Timeout, out cmdDuration);

                    totalDuration += cmdDuration;

                    if(sense || _dev.Error)
                        continue;

                    _resume.BadBlocks.Remove(badSector);
                    extents.Add(badSector);
                    _outputPlugin.WriteSector(readBuffer, badSector);
                }

                EndProgress?.Invoke();
                end = DateTime.UtcNow;
                UpdateStatus?.Invoke($"Trimmming finished in {(end - start).TotalSeconds} seconds.");
                _dumpLog.WriteLine("Trimmming finished in {0} seconds.", (end - start).TotalSeconds);
            }
            #endregion Trimming

            #region Error handling
            if(_resume.BadBlocks.Count > 0 &&
               !_aborted                   &&
               _retryPasses > 0)
            {
                List<ulong> tmpList = new List<ulong>();

                foreach(ulong ur in _resume.BadBlocks)
                    for(ulong i = ur; i < ur + blocksToRead; i++)
                        tmpList.Add(i);

                tmpList.Sort();

                int  pass              = 1;
                bool forward           = true;
                bool runningPersistent = false;

                _resume.BadBlocks = tmpList;
                Modes.ModePage? currentModePage = null;
                byte[]          md6;
                byte[]          md10;

                if(_persistent)
                {
                    Modes.ModePage_01_MMC pgMmc;

                    sense = _dev.ModeSense6(out readBuffer, out _, false, ScsiModeSensePageControl.Current, 0x01,
                                            _dev.Timeout, out _);

                    if(sense)
                    {
                        sense = _dev.ModeSense10(out readBuffer, out _, false, ScsiModeSensePageControl.Current, 0x01,
                                                 _dev.Timeout, out _);

                        if(!sense)
                        {
                            Modes.DecodedMode? dcMode10 =
                                Modes.DecodeMode10(readBuffer, PeripheralDeviceTypes.MultiMediaDevice);

                            if(dcMode10.HasValue)
                                foreach(Modes.ModePage modePage in dcMode10.Value.Pages)
                                    if(modePage.Page    == 0x01 &&
                                       modePage.Subpage == 0x00)
                                        currentModePage = modePage;
                        }
                    }
                    else
                    {
                        Modes.DecodedMode? dcMode6 =
                            Modes.DecodeMode6(readBuffer, PeripheralDeviceTypes.MultiMediaDevice);

                        if(dcMode6.HasValue)
                            foreach(Modes.ModePage modePage in dcMode6.Value.Pages)
                                if(modePage.Page    == 0x01 &&
                                   modePage.Subpage == 0x00)
                                    currentModePage = modePage;
                    }

                    if(currentModePage == null)
                    {
                        pgMmc = new Modes.ModePage_01_MMC
                        {
                            PS = false, ReadRetryCount = 0x20, Parameter = 0x00
                        };

                        currentModePage = new Modes.ModePage
                        {
                            Page = 0x01, Subpage = 0x00, PageResponse = Modes.EncodeModePage_01_MMC(pgMmc)
                        };
                    }

                    pgMmc = new Modes.ModePage_01_MMC
                    {
                        PS = false, ReadRetryCount = 255, Parameter = 0x20
                    };

                    var md = new Modes.DecodedMode
                    {
                        Header = new Modes.ModeHeader(), Pages = new[]
                        {
                            new Modes.ModePage
                            {
                                Page = 0x01, Subpage = 0x00, PageResponse = Modes.EncodeModePage_01_MMC(pgMmc)
                            }
                        }
                    };

                    md6  = Modes.EncodeMode6(md, _dev.ScsiType);
                    md10 = Modes.EncodeMode10(md, _dev.ScsiType);

                    UpdateStatus?.Invoke("Sending MODE SELECT to drive (return damaged blocks).");
                    _dumpLog.WriteLine("Sending MODE SELECT to drive (return damaged blocks).");
                    sense = _dev.ModeSelect(md6, out senseBuf, true, false, _dev.Timeout, out _);

                    if(sense)
                        sense = _dev.ModeSelect10(md10, out senseBuf, true, false, _dev.Timeout, out _);

                    if(sense)
                    {
                        UpdateStatus?.
                            Invoke("Drive did not accept MODE SELECT command for persistent error reading, try another drive.");

                        DicConsole.DebugWriteLine("Error: {0}", Sense.PrettifySense(senseBuf));

                        _dumpLog.
                            WriteLine("Drive did not accept MODE SELECT command for persistent error reading, try another drive.");
                    }
                    else
                        runningPersistent = true;
                }

                InitProgress?.Invoke();
                repeatRetry:
                ulong[] tmpArray = _resume.BadBlocks.ToArray();

                foreach(ulong badSector in tmpArray)
                {
                    if(_aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        UpdateStatus?.Invoke("Aborted!");
                        _dumpLog.WriteLine("Aborted!");

                        break;
                    }

                    PulseProgress?.Invoke(string.Format("Retrying sector {0}, pass {1}, {3}{2}", badSector, pass,
                                                        forward ? "forward" : "reverse",
                                                        runningPersistent ? "recovering partial data, " : ""));

                    sense = _dev.Read12(out readBuffer, out senseBuf, 0, false, false, false, false, (uint)badSector,
                                        BLOCK_SIZE, 0, 1, false, _dev.Timeout, out cmdDuration);

                    totalDuration += cmdDuration;

                    if(!sense &&
                       !_dev.Error)
                    {
                        _resume.BadBlocks.Remove(badSector);
                        extents.Add(badSector);
                        _outputPlugin.WriteSector(readBuffer, badSector);
                        UpdateStatus?.Invoke($"Correctly retried block {badSector} in pass {pass}.");
                        _dumpLog.WriteLine("Correctly retried block {0} in pass {1}.", badSector, pass);
                    }
                    else if(runningPersistent)
                        _outputPlugin.WriteSector(readBuffer, badSector);
                }

                if(pass < _retryPasses &&
                   !_aborted           &&
                   _resume.BadBlocks.Count > 0)
                {
                    pass++;
                    forward = !forward;
                    _resume.BadBlocks.Sort();
                    _resume.BadBlocks.Reverse();

                    goto repeatRetry;
                }

                if(runningPersistent && currentModePage.HasValue)
                {
                    var md = new Modes.DecodedMode
                    {
                        Header = new Modes.ModeHeader(), Pages = new[]
                        {
                            currentModePage.Value
                        }
                    };

                    md6  = Modes.EncodeMode6(md, _dev.ScsiType);
                    md10 = Modes.EncodeMode10(md, _dev.ScsiType);

                    UpdateStatus?.Invoke("Sending MODE SELECT to drive (return device to previous status).");
                    _dumpLog.WriteLine("Sending MODE SELECT to drive (return device to previous status).");
                    sense = _dev.ModeSelect(md6, out senseBuf, true, false, _dev.Timeout, out _);

                    if(sense)
                        _dev.ModeSelect10(md10, out senseBuf, true, false, _dev.Timeout, out _);
                }

                EndProgress?.Invoke();
            }
            #endregion Error handling

            _resume.BadBlocks.Sort();
            currentTry.Extents = ExtentsConverter.ToMetadata(extents);

            foreach(KeyValuePair<MediaTagType, byte[]> tag in mediaTags)
            {
                if(tag.Value is null)
                {
                    DicConsole.ErrorWriteLine("Error: Tag type {0} is null, skipping...", tag.Key);

                    continue;
                }

                ret = _outputPlugin.WriteMediaTag(tag.Value, tag.Key);

                if(ret || _force)
                    continue;

                // Cannot write tag to image
                _dumpLog.WriteLine($"Cannot write tag {tag.Key}.");

                StoppingErrorMessage?.Invoke($"Cannot write tag {tag.Key}." + Environment.NewLine +
                                             _outputPlugin.ErrorMessage);

                return;
            }

            _resume.BadBlocks.Sort();

            foreach(ulong bad in _resume.BadBlocks)
                _dumpLog.WriteLine("Sector {0} could not be read.", bad);

            currentTry.Extents = ExtentsConverter.ToMetadata(extents);

            _outputPlugin.SetDumpHardware(_resume.Tries);

            if(_preSidecar != null)
                _outputPlugin.SetCicmMetadata(_preSidecar);

            _dumpLog.WriteLine("Closing output file.");
            UpdateStatus?.Invoke("Closing output file.");
            DateTime closeStart = DateTime.Now;
            _outputPlugin.Close();
            DateTime closeEnd = DateTime.Now;
            UpdateStatus?.Invoke($"Closed in {(closeEnd - closeStart).TotalSeconds} seconds.");
            _dumpLog.WriteLine("Closed in {0} seconds.", (closeEnd - closeStart).TotalSeconds);

            if(_aborted)
            {
                UpdateStatus?.Invoke("Aborted!");
                _dumpLog.WriteLine("Aborted!");

                return;
            }

            double totalChkDuration = 0;

            if(_metadata)
            {
                var layers = new LayersType
                {
                    type = LayersTypeType.OTP, typeSpecified = true, Sectors = new SectorsType[1]
                };

                layers.Sectors[0] = new SectorsType
                {
                    Value = layerBreak
                };

                WriteOpticalSidecar(BLOCK_SIZE, blocks, dskType, layers, mediaTags, 1, out totalChkDuration, null);
            }

            UpdateStatus?.Invoke("");

            UpdateStatus?.
                Invoke($"Took a total of {(end - start).TotalSeconds:F3} seconds ({totalDuration / 1000:F3} processing commands, {totalChkDuration / 1000:F3} checksumming, {imageWriteDuration:F3} writing, {(closeEnd - closeStart).TotalSeconds:F3} closing).");

            UpdateStatus?.
                Invoke($"Average speed: {((double)BLOCK_SIZE * (double)(blocks + 1)) / 1048576 / (totalDuration / 1000):F3} MiB/sec.");

            UpdateStatus?.Invoke($"Fastest speed burst: {maxSpeed:F3} MiB/sec.");
            UpdateStatus?.Invoke($"Slowest speed burst: {minSpeed:F3} MiB/sec.");
            UpdateStatus?.Invoke($"{_resume.BadBlocks.Count} sectors could not be read.");
            UpdateStatus?.Invoke("");

            Statistics.AddMedia(dskType, true);
        }
    }
}