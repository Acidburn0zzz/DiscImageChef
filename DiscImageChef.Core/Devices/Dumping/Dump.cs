using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using DiscImageChef.CommonTypes.Enums;
using DiscImageChef.CommonTypes.Interfaces;
using DiscImageChef.CommonTypes.Metadata;
using DiscImageChef.Core.Logging;
using DiscImageChef.Devices;
using Schemas;

namespace DiscImageChef.Core.Devices.Dumping
{
    public partial class Dump
    {
        readonly Device                     _dev;
        readonly string                     _devicePath;
        readonly bool                       _doResume;
        readonly DumpLog                    _dumpLog;
        readonly bool                       _dumpRaw;
        readonly Encoding                   _encoding;
        readonly bool                       _force;
        readonly Dictionary<string, string> _formatOptions;
        readonly bool                       _nometadata;
        readonly bool                       _notrim;
        readonly string                     _outputPath;
        readonly IWritableImage             _outputPlugin;
        readonly string                     _outputPrefix;
        readonly bool                       _persistent;
        readonly CICMMetadataType           _preSidecar;
        readonly ushort                     _retryPasses;
        readonly bool                       _stopOnError;
        bool                                _aborted;
        readonly bool                       _debug;
        bool                                _dumpFirstTrackPregap;
        bool                                _fixOffset;
        Resume                              _resume;
        Sidecar                             _sidecarClass;
        uint                                _skip;

        /// <summary>Initializes dumpers</summary>
        /// <param name="doResume">Should resume?</param>
        /// <param name="dev">Device</param>
        /// <param name="devicePath">Path to the device</param>
        /// <param name="outputPrefix">Prefix for output log files</param>
        /// <param name="outputPlugin">Plugin for output file</param>
        /// <param name="retryPasses">How many times to retry</param>
        /// <param name="force">Force to continue dump whenever possible</param>
        /// <param name="dumpRaw">Dump long sectors</param>
        /// <param name="persistent">Store whatever data the drive returned on error</param>
        /// <param name="stopOnError">Stop dump on first error</param>
        /// <param name="resume">Information for dump resuming</param>
        /// <param name="dumpLog">Dump logger</param>
        /// <param name="encoding">Encoding to use when analyzing dump</param>
        /// <param name="outputPath">Path to output file</param>
        /// <param name="formatOptions">Formats to pass to output file plugin</param>
        /// <param name="notrim">Do not trim errors from skipped sectors</param>
        /// <param name="dumpFirstTrackPregap">Try to read and dump as much first track pregap as possible</param>
        /// <param name="preSidecar">Sidecar to store in dumped image</param>
        /// <param name="skip">How many sectors to skip reading on error</param>
        /// <param name="nometadata">Create metadata sidecar after dump?</param>
        public Dump(bool doResume, Device dev, string devicePath, IWritableImage outputPlugin, ushort retryPasses,
                    bool force, bool dumpRaw, bool persistent, bool stopOnError, Resume resume, DumpLog dumpLog,
                    Encoding encoding, string outputPrefix, string outputPath, Dictionary<string, string> formatOptions,
                    CICMMetadataType preSidecar, uint skip, bool nometadata, bool notrim, bool dumpFirstTrackPregap,
                    bool fixOffset, bool debug)
        {
            _doResume             = doResume;
            _dev                  = dev;
            _devicePath           = devicePath;
            _outputPlugin         = outputPlugin;
            _retryPasses          = retryPasses;
            _force                = force;
            _dumpRaw              = dumpRaw;
            _persistent           = persistent;
            _stopOnError          = stopOnError;
            _resume               = resume;
            _dumpLog              = dumpLog;
            _encoding             = encoding;
            _outputPrefix         = outputPrefix;
            _outputPath           = outputPath;
            _formatOptions        = formatOptions;
            _preSidecar           = preSidecar;
            _skip                 = skip;
            _nometadata           = nometadata;
            _notrim               = notrim;
            _dumpFirstTrackPregap = dumpFirstTrackPregap;
            _aborted              = false;
            _fixOffset            = fixOffset;
            _debug                = debug;
        }

        /// <summary>Starts dumping with the stablished fields and autodetecting the device type</summary>
        public void Start()
        {
            if(_dev.IsUsb                 &&
               _dev.UsbVendorId == 0x054C &&
               (_dev.UsbProductId == 0x01C8 || _dev.UsbProductId == 0x01C9 || _dev.UsbProductId == 0x02D2))
                PlayStationPortable();
            else
                switch(_dev.Type)
                {
                    case DeviceType.ATA:
                        Ata();

                        break;
                    case DeviceType.MMC:
                    case DeviceType.SecureDigital:
                        SecureDigital();

                        break;
                    case DeviceType.NVMe:
                        NVMe();

                        break;
                    case DeviceType.ATAPI:
                    case DeviceType.SCSI:
                        Scsi();

                        break;
                    default:
                        _dumpLog.WriteLine("Unknown device type.");
                        _dumpLog.Close();
                        StoppingErrorMessage?.Invoke("Unknown device type.");

                        return;
                }

            _dumpLog.Close();

            if(_resume == null ||
               !_doResume)
                return;

            _resume.LastWriteDate = DateTime.UtcNow;
            _resume.BadBlocks.Sort();

            if(File.Exists(_outputPrefix + ".resume.xml"))
                File.Delete(_outputPrefix + ".resume.xml");

            var fs = new FileStream(_outputPrefix + ".resume.xml", FileMode.Create, FileAccess.ReadWrite);
            var xs = new XmlSerializer(_resume.GetType());
            xs.Serialize(fs, _resume);
            fs.Close();
        }

        public void Abort()
        {
            _aborted = true;
            _sidecarClass?.Abort();
        }

        /// <summary>Event raised when the progress bar is not longer needed</summary>
        public event EndProgressHandler EndProgress;
        /// <summary>Event raised when a progress bar is needed</summary>
        public event InitProgressHandler InitProgress;
        /// <summary>Event raised to report status updates</summary>
        public event UpdateStatusHandler UpdateStatus;
        /// <summary>Event raised to report a non-fatal error</summary>
        public event ErrorMessageHandler ErrorMessage;
        /// <summary>Event raised to report a fatal error that stops the dumping operation and should call user's attention</summary>
        public event ErrorMessageHandler StoppingErrorMessage;
        /// <summary>Event raised to update the values of a determinate progress bar</summary>
        public event UpdateProgressHandler UpdateProgress;
        /// <summary>Event raised to update the status of an undeterminate progress bar</summary>
        public event PulseProgressHandler PulseProgress;
        /// <summary>Event raised when the progress bar is not longer needed</summary>
        public event EndProgressHandler2 EndProgress2;
        /// <summary>Event raised when a progress bar is needed</summary>
        public event InitProgressHandler2 InitProgress2;
        /// <summary>Event raised to update the values of a determinate progress bar</summary>
        public event UpdateProgressHandler2 UpdateProgress2;
    }
}