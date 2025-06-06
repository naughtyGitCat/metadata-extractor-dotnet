﻿// Copyright (c) Drew Noakes and contributors. All Rights Reserved. Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using MetadataExtractor.Formats.Exif.Makernotes;
using MetadataExtractor.Formats.GeoTiff;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Photoshop;
using MetadataExtractor.Formats.Tiff;
using MetadataExtractor.Formats.Xmp;

namespace MetadataExtractor.Formats.Exif
{
    /// <summary>
    /// Implementation of <see cref="ITiffHandler"/> used for handling TIFF tags according to the Exif standard.
    /// <para />
    /// Includes support for camera manufacturer makernotes.
    /// </summary>
    /// <author>Drew Noakes https://drewnoakes.com</author>
    public class ExifTiffHandler : DirectoryTiffHandler
    {
        private readonly int _exifStartOffset;

        public ExifTiffHandler(List<Directory> directories, int exifStartOffset)
            : base(directories)
        {
            _exifStartOffset = exifStartOffset;
        }

        /// <exception cref="TiffProcessingException"/>
        public override TiffStandard ProcessTiffMarker(ushort marker)
        {
#pragma warning disable format

            const ushort StandardTiffMarker     = 0x002A;
            const ushort BigTiffMarker          = 0x002B;
            const ushort OlympusRawTiffMarker   = 0x4F52; // for ORF files
            const ushort OlympusRawTiffMarker2  = 0x5352; // for ORF files
            const ushort PanasonicRawTiffMarker = 0x0055; // for RAW, RW2, and RWL files

#pragma warning restore format

            switch (marker)
            {
                case StandardTiffMarker:
                case BigTiffMarker:
                case OlympusRawTiffMarker:  // Todo: implement an IFD0
                case OlympusRawTiffMarker2: // Todo: implement an IFD0
                    PushDirectory(new ExifIfd0Directory());
                    break;
                case PanasonicRawTiffMarker:
                    PushDirectory(new PanasonicRawIfd0Directory());
                    break;
                default:
                    throw new TiffProcessingException($"Unexpected TIFF marker: 0x{marker:X}");
            }

            return marker == BigTiffMarker
                ? TiffStandard.BigTiff
                : TiffStandard.Tiff;
        }

        public override bool TryEnterSubIfd(int tagId)
        {
            if (tagId == ExifDirectoryBase.TagSubIfdOffset)
            {
                PushDirectory(new ExifSubIfdDirectory());
                return true;
            }

            if (CurrentDirectory is ExifIfd0Directory or PanasonicRawIfd0Directory)
            {
                if (tagId == ExifIfd0Directory.TagExifSubIfdOffset)
                {
                    PushDirectory(new ExifSubIfdDirectory());
                    return true;
                }
                if (tagId == ExifIfd0Directory.TagGpsInfoOffset)
                {
                    PushDirectory(new GpsDirectory());
                    return true;
                }
            }
            else if (CurrentDirectory is ExifSubIfdDirectory)
            {
                if (tagId == ExifSubIfdDirectory.TagInteropOffset)
                {
                    PushDirectory(new ExifInteropDirectory());
                    return true;
                }
            }
            else if (CurrentDirectory is OlympusMakernoteDirectory)
            {
                // Note: these also appear in CustomProcessTag because some are IFD pointers while others begin immediately
                // for the same directories
                switch (tagId)
                {
                    case OlympusMakernoteDirectory.TagEquipment:
                        PushDirectory(new OlympusEquipmentMakernoteDirectory());
                        return true;
                    case OlympusMakernoteDirectory.TagCameraSettings:
                        PushDirectory(new OlympusCameraSettingsMakernoteDirectory());
                        return true;
                    case OlympusMakernoteDirectory.TagRawDevelopment:
                        PushDirectory(new OlympusRawDevelopmentMakernoteDirectory());
                        return true;
                    case OlympusMakernoteDirectory.TagRawDevelopment2:
                        PushDirectory(new OlympusRawDevelopment2MakernoteDirectory());
                        return true;
                    case OlympusMakernoteDirectory.TagImageProcessing:
                        PushDirectory(new OlympusImageProcessingMakernoteDirectory());
                        return true;
                    case OlympusMakernoteDirectory.TagFocusInfo:
                        PushDirectory(new OlympusFocusInfoMakernoteDirectory());
                        return true;
                    case OlympusMakernoteDirectory.TagRawInfo:
                        PushDirectory(new OlympusRawInfoMakernoteDirectory());
                        return true;
                    case OlympusMakernoteDirectory.TagMainInfo:
                        PushDirectory(new OlympusMakernoteDirectory());
                        return true;
                }
            }

            return false;
        }

        public override bool HasFollowerIfd()
        {
            // If the next IFD is IFD1, it's a thumbnail for JPG and some TIFF-based images
            // NOTE: this is not true for some other image types, but those are not implemented yet
            if (CurrentDirectory is ExifIfd0Directory)
            {
                PushDirectory(new ExifThumbnailDirectory(_exifStartOffset));
                return true;
            }
            else
            {
                // In multipage TIFFs, the 'follower' IFD points to the next image in the set
                PushDirectory(new ExifImageDirectory());
                return true;
            }
        }

        public override bool CustomProcessTag(in TiffReaderContext context, int tagId, int valueOffset, int byteCount)
        {
            // Some 0x0000 tags have a 0 byteCount. Determine whether it's bad.
            if (tagId == 0)
            {
                if (CurrentDirectory!.ContainsTag(tagId))
                {
                    // Let it go through for now. Some directories handle it, some don't.
                    return false;
                }

                // Skip over 0x0000 tags that don't have any associated bytes. No idea what it contains in this case, if anything.
                if (byteCount == 0)
                    return true;
            }

            // Custom processing for the Makernote tag
            if (tagId == ExifDirectoryBase.TagMakernote && CurrentDirectory is ExifSubIfdDirectory)
                return ProcessMakernote(in context, valueOffset);

            // Custom processing for embedded IPTC data
            if (tagId == ExifDirectoryBase.TagIptcNaa && CurrentDirectory is ExifIfd0Directory)
            {
                // NOTE Adobe sets type 4 for IPTC instead of 7
                if (context.Reader.GetSByte(valueOffset) == 0x1c)
                {
                    var iptcBytes = context.Reader.GetBytes(valueOffset, byteCount);
                    var iptcDirectory = new IptcReader().Extract(new SequentialByteArrayReader(iptcBytes), iptcBytes.Length);
                    iptcDirectory.Parent = CurrentDirectory;
                    Directories.Add(iptcDirectory);
                    return true;
                }
                return false;
            }

            // Custom processing for ICC Profile data
            if (tagId == ExifDirectoryBase.TagInterColorProfile)
            {
                var iccBytes = context.Reader.GetBytes(valueOffset, byteCount);
                var iccDirectory = new IccReader().Extract(new ByteArrayReader(iccBytes));
                iccDirectory.Parent = CurrentDirectory;
                Directories.Add(iccDirectory);
                return true;
            }

            // Custom processing for Photoshop data
            if (tagId == ExifDirectoryBase.TagPhotoshopSettings && CurrentDirectory is ExifIfd0Directory)
            {
                var photoshopBytes = context.Reader.GetBytes(valueOffset, byteCount);
                var photoshopDirectories = new PhotoshopReader().Extract(new SequentialByteArrayReader(photoshopBytes), byteCount);
                if (photoshopDirectories.Count != 0)
                {
                    // Could be any number of directories. Only assign the Parent to the PhotoshopDirectory
                    photoshopDirectories.OfType<PhotoshopDirectory>().First().Parent = CurrentDirectory;
                    Directories.AddRange(photoshopDirectories);
                }
                return true;
            }

            // Custom processing for embedded XMP data
            if (tagId == ExifDirectoryBase.TagApplicationNotes && CurrentDirectory is ExifIfd0Directory or ExifSubIfdDirectory)
            {
                var xmpDirectory = new XmpReader().Extract(context.Reader.GetNullTerminatedBytes(valueOffset, byteCount));
                xmpDirectory.Parent = CurrentDirectory;
                Directories.Add(xmpDirectory);
                return true;
            }

            // Custom processing for Apple RunTime tag
            if (tagId == AppleMakernoteDirectory.TagRunTime && CurrentDirectory is AppleMakernoteDirectory)
            {
                var bytes = context.Reader.GetBytes(valueOffset, byteCount);
                var directory = AppleRunTimeMakernoteDirectory.Parse(bytes);
                directory.Parent = CurrentDirectory;
                Directories.Add(directory);
                return true;
            }

            if (HandlePrintIM(CurrentDirectory!, tagId))
            {
                var printIMDirectory = new PrintIMDirectory { Parent = CurrentDirectory };
                Directories.Add(printIMDirectory);
                ProcessPrintIM(printIMDirectory, valueOffset, context.Reader, byteCount);
                return true;
            }

            // Note: these also appear in TryEnterSubIfd because some are IFD pointers while others begin immediately
            // for the same directories
            if (CurrentDirectory is OlympusMakernoteDirectory)
            {
                switch (tagId)
                {
                    case OlympusMakernoteDirectory.TagEquipment:
                        PushDirectory(new OlympusEquipmentMakernoteDirectory());
                        TiffReader.ProcessIfd(this, context, valueOffset);
                        return true;
                    case OlympusMakernoteDirectory.TagCameraSettings:
                        PushDirectory(new OlympusCameraSettingsMakernoteDirectory());
                        TiffReader.ProcessIfd(this, context, valueOffset);
                        return true;
                    case OlympusMakernoteDirectory.TagRawDevelopment:
                        PushDirectory(new OlympusRawDevelopmentMakernoteDirectory());
                        TiffReader.ProcessIfd(this, context, valueOffset);
                        return true;
                    case OlympusMakernoteDirectory.TagRawDevelopment2:
                        PushDirectory(new OlympusRawDevelopment2MakernoteDirectory());
                        TiffReader.ProcessIfd(this, context, valueOffset);
                        return true;
                    case OlympusMakernoteDirectory.TagImageProcessing:
                        PushDirectory(new OlympusImageProcessingMakernoteDirectory());
                        TiffReader.ProcessIfd(this, context, valueOffset);
                        return true;
                    case OlympusMakernoteDirectory.TagFocusInfo:
                        PushDirectory(new OlympusFocusInfoMakernoteDirectory());
                        TiffReader.ProcessIfd(this, context, valueOffset);
                        return true;
                    case OlympusMakernoteDirectory.TagRawInfo:
                        PushDirectory(new OlympusRawInfoMakernoteDirectory());
                        TiffReader.ProcessIfd(this, context, valueOffset);
                        return true;
                    case OlympusMakernoteDirectory.TagMainInfo:
                        PushDirectory(new OlympusMakernoteDirectory());
                        TiffReader.ProcessIfd(this, context, valueOffset);
                        return true;
                }
            }
            else if (CurrentDirectory is PanasonicRawIfd0Directory)
            {
                // these contain binary data with specific offsets, and can't be processed as regular IFD's.
                // The binary data is broken into 'fake' tags and there is a pattern.
                switch (tagId)
                {
                    case PanasonicRawIfd0Directory.TagWbInfo:
                        var dirWbInfo = new PanasonicRawWbInfoDirectory { Parent = CurrentDirectory };
                        Directories.Add(dirWbInfo);
                        ProcessBinary(dirWbInfo, valueOffset, context.Reader, byteCount, isSigned: false, arrayLength: 2);
                        return true;
                    case PanasonicRawIfd0Directory.TagWbInfo2:
                        var dirWbInfo2 = new PanasonicRawWbInfo2Directory { Parent = CurrentDirectory };
                        Directories.Add(dirWbInfo2);
                        ProcessBinary(dirWbInfo2, valueOffset, context.Reader, byteCount, isSigned: false, arrayLength: 3);
                        return true;
                    case PanasonicRawIfd0Directory.TagDistortionInfo:
                        var dirDistort = new PanasonicRawDistortionDirectory { Parent = CurrentDirectory };
                        Directories.Add(dirDistort);
                        ProcessBinary(dirDistort, valueOffset, context.Reader, byteCount, isSigned: true, arrayLength: 1);
                        return true;
                }

                // Panasonic RAW sometimes contains an embedded version of the data as a JPG file.
                if (tagId == PanasonicRawIfd0Directory.TagJpgFromRaw)
                {
                    // Extract information from embedded image since it is metadata-rich
                    var bytes = context.Reader.GetBytes(valueOffset, byteCount);
                    var stream = new MemoryStream(bytes);

                    foreach (var directory in JpegMetadataReader.ReadMetadata(stream))
                    {
                        directory.Parent = CurrentDirectory;
                        Directories.Add(directory);
                    }

                    return true;
                }

                static void ProcessBinary(Directory directory, int tagValueOffset, IndexedReader reader, int byteCount, bool isSigned, int arrayLength)
                {
                    // expects signed/unsigned int16 (for now)
                    var byteSize = isSigned ? sizeof(short) : sizeof(ushort);

                    // 'directory' is assumed to contain tags that correspond to the byte position unless it's a set of bytes
                    for (var i = 0; i < byteCount; i++)
                    {
                        if (directory.HasTagName(i))
                        {
                            // only process this tag if the 'next' integral tag exists. Otherwise, it's a set of bytes
                            if (i < byteCount - 1 && directory.HasTagName(i + 1))
                            {
                                if (isSigned)
                                    directory.Set(i, reader.GetInt16(tagValueOffset + i * byteSize));
                                else
                                    directory.Set(i, reader.GetUInt16(tagValueOffset + i * byteSize));
                            }
                            else
                            {
                                // the next arrayLength bytes are a multi-byte value
                                if (isSigned)
                                {
                                    var val = new short[arrayLength];
                                    for (var j = 0; j < val.Length; j++)
                                        val[j] = reader.GetInt16(tagValueOffset + (i + j) * byteSize);
                                    directory.Set(i, val);
                                }
                                else
                                {
                                    var val = new ushort[arrayLength];
                                    for (var j = 0; j < val.Length; j++)
                                        val[j] = reader.GetUInt16(tagValueOffset + (i + j) * byteSize);
                                    directory.Set(i, val);
                                }

                                i += arrayLength - 1;
                            }
                        }

                    }
                }
            }

            if (tagId is NikonType2MakernoteDirectory.TagPictureControl or NikonType2MakernoteDirectory.TagPictureControl2 && CurrentDirectory is NikonType2MakernoteDirectory)
            {
                if (byteCount == 58)
                {
                    var bytes = context.Reader.GetBytes(valueOffset, byteCount);
                    var directory = NikonPictureControl1Directory.FromBytes(bytes);
                    directory.Parent = CurrentDirectory;
                    Directories.Add(directory);
                    return true;
                }
                else if (byteCount == 68)
                {
                    var bytes = context.Reader.GetBytes(valueOffset, byteCount);
                    var directory = NikonPictureControl2Directory.FromBytes(bytes);
                    directory.Parent = CurrentDirectory;
                    Directories.Add(directory);
                    return true;
                }
                else if (byteCount == 74)
                {
                    // TODO NikonPictureControl3Directory
                }
            }

            return false;
        }

        public override void EndingIfd(in TiffReaderContext context)
        {
            if (CurrentDirectory is ExifIfd0Directory directory)
            {
                if (directory.GetObject(ExifDirectoryBase.TagGeoTiffGeoKeys) is ushort[] geoKeys)
                {
                    // GetTIFF stores data in its own format within TIFF. It is TIFF-like, but different.
                    // It can reference data from tags that have not been visited yet, so we must unpack it
                    // once the directory is complete.
                    ProcessGeoTiff(geoKeys, directory);
                }
            }

            base.EndingIfd(context);
        }

#if NET7_0_OR_GREATER
        [UnconditionalSuppressMessage("Aot", "IL3050:RequiresDynamicCode", Justification = "Array.CreateInstance is only called with known element types")]
#endif
        private void ProcessGeoTiff(ushort[] geoKeys, ExifIfd0Directory sourceDirectory)
        {
            if (geoKeys.Length < 4)
                return;

            var geoTiffDirectory = new GeoTiffDirectory { Parent = CurrentDirectory };
            Directories.Add(geoTiffDirectory);

            var i = 0;

            var directoryVersion = geoKeys[i++];
            var revision = geoKeys[i++];
            var minorRevision = geoKeys[i++];
            var numberOfKeys = geoKeys[i++];

            // TODO store these values in negative tag IDs

            var sourceTags = new HashSet<int> { ExifDirectoryBase.TagGeoTiffGeoKeys };

            for (var j = 0; j < numberOfKeys; j++)
            {
                var keyId = geoKeys[i++];
                var tiffTagLocation = geoKeys[i++];
                var valueCount = geoKeys[i++];
                var valueOffset = geoKeys[i++];

                if (tiffTagLocation == 0)
                {
                    // Identifies the tag containing the value. If zero, then the value is ushort and stored
                    // in valueOffset directly, and the value count is implied as 1.
                    geoTiffDirectory.Set(keyId, valueOffset);
                }
                else
                {
                    // The value is stored in another tag.
                    var sourceTagId = tiffTagLocation;
                    sourceTags.Add(sourceTagId);
                    var sourceValue = sourceDirectory.GetObject(sourceTagId);
                    if (sourceValue is StringValue sourceString)
                    {
                        if (valueOffset + valueCount <= sourceString.Bytes.Length)
                        {
                            // ASCII values appear to have a | character and the end, so we trim it off here
                            geoTiffDirectory.Set(keyId, sourceString.ToString(valueOffset, valueCount).TrimEnd('|'));
                        }
                        else
                        {
                            geoTiffDirectory.AddError($"GeoTIFF key {keyId} with offset {valueOffset} and count {valueCount} extends beyond length of source value ({sourceString.Bytes.Length})");
                        }
                    }
                    else if (sourceValue is Array sourceArray)
                    {
                        if (valueOffset + valueCount <= sourceArray.Length)
                        {
                            Type? elementType = sourceArray.GetType().GetElementType();
                            var array = Array.CreateInstance(elementType!, valueCount);
                            Array.Copy(sourceArray, valueOffset, array, 0, valueCount);
                            geoTiffDirectory.Set(keyId, array);
                        }
                        else
                        {
                            geoTiffDirectory.AddError($"GeoTIFF key {keyId} with offset {valueOffset} and count {valueCount} extends beyond length of source value ({sourceArray.Length})");
                        }
                    }
                    else
                    {
                        geoTiffDirectory.AddError($"GeoTIFF key {keyId} references tag {sourceTagId} which has unsupported type of {sourceValue?.GetType().ToString() ?? "null"}");
                    }
                }
            }

            foreach (var sourceTag in sourceTags)
            {
                sourceDirectory.RemoveTag(sourceTag);
            }
        }

        public override bool TryCustomProcessFormat(int tagId, TiffDataFormatCode formatCode, ulong componentCount, out ulong byteCount)
        {
            if ((ushort)formatCode == 13u)
            {
                byteCount = 4L * componentCount;
                return true;
            }

            // an unknown (0) formatCode needs to be potentially handled later as a highly custom directory tag
            if (formatCode == 0)
            {
                byteCount = 0;
                return true;
            }

            byteCount = default;
            return false;
        }

        /// <exception cref="IOException"/>
        private bool ProcessMakernote(in TiffReaderContext context, int makernoteOffset)
        {
            Debug.Assert(makernoteOffset >= 0, "makernoteOffset >= 0");

            var cameraMake = Directories.OfType<ExifIfd0Directory>().FirstOrDefault()?.GetString(ExifDirectoryBase.TagMake);

            int headerSize = (int)Math.Min(14, context.Reader.Length - makernoteOffset);
            Span<byte> headerBytes = stackalloc byte[headerSize];
            context.Reader.GetBytes(makernoteOffset, headerBytes);

            string headerString = Encoding.ASCII.GetString(headerBytes);

            if (headerBytes.StartsWith("OLYMP\0"u8) ||
                headerBytes.StartsWith("EPSON"u8) ||
                headerBytes.StartsWith("AGFA"u8))
            {
                // Olympus Makernote
                // Epson and Agfa use Olympus makernote standard: http://www.ozhiker.com/electronics/pjmt/jpeg_info/
                PushDirectory(new OlympusMakernoteDirectory());
                TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset + 8);
            }
            else if (headerBytes.StartsWith("OLYMPUS\0II"u8))
            {
                // Olympus Makernote (alternate)
                // Note that data is relative to the beginning of the makernote
                // http://exiv2.org/makernote.html
                PushDirectory(new OlympusMakernoteDirectory());
                TiffReader.ProcessIfd(this, context.WithShiftedBaseOffset(makernoteOffset), ifdOffset: 12);
            }
            else if (headerBytes.StartsWith("OM SYSTEM\0\0\0II"u8))
            {
                // Olympus Makernote (OM SYSTEM)
                // Note that data is relative to the beginning of the makernote
                // http://exiv2.org/makernote.html
                PushDirectory(new OlympusMakernoteDirectory());
                TiffReader.ProcessIfd(this, context.WithShiftedBaseOffset(makernoteOffset), ifdOffset: 16);
            }
            else if (cameraMake is not null && cameraMake.StartsWith("MINOLTA", StringComparison.OrdinalIgnoreCase))
            {
                // Cases seen with the model starting with MINOLTA in capitals seem to have a valid Olympus makernote
                // area that commences immediately.
                PushDirectory(new OlympusMakernoteDirectory());
                TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset);
            }
            else if (cameraMake is not null && cameraMake.AsSpan().TrimStart().StartsWith("NIKON".AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                if (headerBytes.StartsWith("Nikon"u8))
                {
                    switch (context.Reader.GetByte(makernoteOffset + 6))
                    {
                        case 1:
                        {
                            /* There are two scenarios here:
                             * Type 1:                  **
                             * :0000: 4E 69 6B 6F 6E 00 01 00-05 00 02 00 02 00 06 00 Nikon...........
                             * :0010: 00 00 EC 02 00 00 03 00-03 00 01 00 00 00 06 00 ................
                             * Type 3:                  **
                             * :0000: 4E 69 6B 6F 6E 00 02 00-00 00 4D 4D 00 2A 00 00 Nikon....MM.*...
                             * :0010: 00 08 00 1E 00 01 00 07-00 00 00 04 30 32 30 30 ............0200
                             */
                            PushDirectory(new NikonType1MakernoteDirectory());
                            TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset + 8);
                            break;
                        }
                        case 2:
                        {
                            PushDirectory(new NikonType2MakernoteDirectory());
                            TiffReader.ProcessIfd(this, context.WithShiftedBaseOffset(makernoteOffset + 10), ifdOffset: 8);
                            break;
                        }
                        default:
                        {
                            Error("Unsupported Nikon makernote data ignored.");
                            break;
                        }
                    }
                }
                else
                {
                    // The IFD begins with the first Makernote byte (no ASCII name).  This occurs with CoolPix 775, E990 and D1 models.
                    PushDirectory(new NikonType2MakernoteDirectory());
                    TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset);
                }
            }
            else if (headerBytes.StartsWith("SONY CAM"u8) ||
                     headerBytes.StartsWith("SONY DSC"u8))
            {
                PushDirectory(new SonyType1MakernoteDirectory());
                TiffReader.ProcessIfd(this, context, makernoteOffset + 12);
            }
            // Do this check LAST after most other Sony checks
            else if (cameraMake is not null && cameraMake.StartsWith("SONY", StringComparison.Ordinal) &&
                (headerBytes[0] != 0x01 || headerBytes[1] != 0x00))
            {
                // The IFD begins with the first Makernote byte (no ASCII name). Used in SR2 and ARW images
                PushDirectory(new SonyType1MakernoteDirectory());
                TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset);
            }
            else if (headerBytes.StartsWith("SEMC MS\0\0\0\0\0"u8))
            {
                // Force Motorola byte order for this directory
                // skip 12 byte header + 2 for "MM" + 6
                PushDirectory(new SonyType6MakernoteDirectory());
                TiffReader.ProcessIfd(this, context.WithByteOrder(isMotorolaByteOrder: true), ifdOffset: makernoteOffset + 20);
            }
            else if (headerBytes.StartsWith("SIGMA\0\0\0"u8) ||
                     headerBytes.StartsWith("FOVEON\0\0"u8))
            {
                PushDirectory(new SigmaMakernoteDirectory());
                TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset + 10);
            }
            else if (headerBytes.StartsWith("KDK"u8))
            {
                var directory = new KodakMakernoteDirectory();
                Directories.Add(directory);
                ProcessKodakMakernote(directory, makernoteOffset, context.Reader.WithByteOrder(isMotorolaByteOrder: headerBytes.StartsWith("KDK INFO"u8)));
            }
            else if (cameraMake is not null && cameraMake.Equals("CANON", StringComparison.OrdinalIgnoreCase)) // Observed "Canon"
            {
                PushDirectory(new CanonMakernoteDirectory());
                TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset);
            }
            else if (cameraMake is not null && cameraMake.StartsWith("CASIO", StringComparison.OrdinalIgnoreCase))
            {
                if (headerBytes.StartsWith("QVC\0\0\0"u8))
                {
                    PushDirectory(new CasioType2MakernoteDirectory());
                    TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset + 6);
                }
                else
                {
                    PushDirectory(new CasioType1MakernoteDirectory());
                    TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset);
                }
            }
            else if (headerBytes.StartsWith("FUJIFILM"u8) || string.Equals("FUJIFILM", cameraMake, StringComparison.OrdinalIgnoreCase))
            {
                // Note that this also applies to certain Leica cameras, such as the Digilux-4.3.
                // The 4 bytes after "FUJIFILM" in the makernote point to the start of the makernote
                // IFD, though the offset is relative to the start of the makernote, not the TIFF header
                var makernoteContext = context.WithShiftedBaseOffset(makernoteOffset).WithByteOrder(isMotorolaByteOrder: false);
                var ifdStart = makernoteContext.Reader.GetInt32(8);
                PushDirectory(new FujifilmMakernoteDirectory());
                TiffReader.ProcessIfd(this, makernoteContext, ifdStart);
            }
            else if (headerBytes.StartsWith("KYOCERA"u8))
            {
                // http://www.ozhiker.com/electronics/pjmt/jpeg_info/kyocera_mn.html
                PushDirectory(new KyoceraMakernoteDirectory());
                TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset + 22);
            }
            else if (headerBytes.StartsWith("LEICA"u8))
            {
                // used by the X1/X2/X VARIO/T
                // (X1 starts with "LEICA\0\x01\0", Make is "LEICA CAMERA AG")
                // (X2 starts with "LEICA\0\x05\0", Make is "LEICA CAMERA AG")
                // (X VARIO starts with "LEICA\0\x04\0", Make is "LEICA CAMERA AG")
                // (T (Typ 701) starts with "LEICA\0\0x6", Make is "LEICA CAMERA AG")
                // (X (Typ 113) starts with "LEICA\0\0x7", Make is "LEICA CAMERA AG")

                if (headerBytes.StartsWith("LEICA\0\x1\0"u8) ||
                    headerBytes.StartsWith("LEICA\0\x4\0"u8) ||
                    headerBytes.StartsWith("LEICA\0\x5\0"u8) ||
                    headerBytes.StartsWith("LEICA\0\x6\0"u8) ||
                    headerBytes.StartsWith("LEICA\0\x7\0"u8))
                {
                    PushDirectory(new LeicaType5MakernoteDirectory());
                    TiffReader.ProcessIfd(this, context.WithShiftedBaseOffset(makernoteOffset), ifdOffset: 8);
                }
                else if (string.Equals("Leica Camera AG", cameraMake, StringComparison.Ordinal))
                {
                    PushDirectory(new LeicaMakernoteDirectory());
                    TiffReader.ProcessIfd(this, context.WithByteOrder(isMotorolaByteOrder: false), ifdOffset: makernoteOffset + 8);
                }
                else if (string.Equals("LEICA", cameraMake, StringComparison.Ordinal))
                {
                    // Some Leica cameras use Panasonic makernote tags
                    PushDirectory(new PanasonicMakernoteDirectory());
                    TiffReader.ProcessIfd(this, context.WithByteOrder(isMotorolaByteOrder: false), ifdOffset: makernoteOffset + 8);
                }
                else
                {
                    return false;
                }
            }
            else if (headerBytes.StartsWith("Panasonic\0\0\0"u8))
            {
                // NON-Standard TIFF IFD Data using Panasonic Tags. There is no Next-IFD pointer after the IFD
                // Offsets are relative to the start of the TIFF header at the beginning of the EXIF segment
                // more information here: http://www.ozhiker.com/electronics/pjmt/jpeg_info/panasonic_mn.html
                PushDirectory(new PanasonicMakernoteDirectory());
                TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset + 12);
            }
            else if (headerBytes.StartsWith("AOC\0"u8))
            {
                // NON-Standard TIFF IFD Data using Casio Type 2 Tags
                // IFD has no Next-IFD pointer at end of IFD, and
                // Offsets are relative to the start of the current IFD tag, not the TIFF header
                // Observed for:
                // - Pentax ist D
                PushDirectory(new CasioType2MakernoteDirectory());
                TiffReader.ProcessIfd(this, context.WithShiftedBaseOffset(makernoteOffset), ifdOffset: 6);
            }
            else if (cameraMake is not null && (cameraMake.StartsWith("PENTAX", StringComparison.OrdinalIgnoreCase) || cameraMake.StartsWith("ASAHI", StringComparison.OrdinalIgnoreCase)))
            {
                // NON-Standard TIFF IFD Data using Pentax Tags
                // IFD has no Next-IFD pointer at end of IFD, and
                // Offsets are relative to the start of the current IFD tag, not the TIFF header
                // Observed for:
                // - PENTAX Optio 330
                // - PENTAX Optio 430
                PushDirectory(new PentaxMakernoteDirectory());
                TiffReader.ProcessIfd(this, context.WithShiftedBaseOffset(makernoteOffset), ifdOffset: 0);
            }
            /*
            else if (headerBytes.StartsWith("KC"u8) || headerBytes.StartsWith("MINOL"u8) || headerBytes.StartsWith("MLY"u8) || headerBytes.StartsWith("+M+M+M+M"u8))
            {
                // This Konica data is not understood.  Header identified in accordance with information at this site:
                // http://www.ozhiker.com/electronics/pjmt/jpeg_info/minolta_mn.html
                // TODO add support for minolta/konica cameras
                exifDirectory.addError("Unsupported Konica/Minolta data ignored.");
            }
            */
            else if (headerBytes.StartsWith("SANYO\x0\x1\x0"u8))
            {
                PushDirectory(new SanyoMakernoteDirectory());
                TiffReader.ProcessIfd(this, context.WithShiftedBaseOffset(makernoteOffset), ifdOffset: 8);
            }
            else if (cameraMake is not null && cameraMake.StartsWith("RICOH", StringComparison.OrdinalIgnoreCase))
            {
                if (headerBytes.StartsWith("Rv"u8) ||
                    headerBytes.StartsWith("Rev"u8))
                {
                    // This is a textual format, where the makernote bytes look like:
                    //   Rv0103;Rg1C;Bg18;Ll0;Ld0;Aj0000;Bn0473800;Fp2E00:������������������������������
                    //   Rv0103;Rg1C;Bg18;Ll0;Ld0;Aj0000;Bn0473800;Fp2D05:������������������������������
                    //   Rv0207;Sf6C84;Rg76;Bg60;Gg42;Ll0;Ld0;Aj0004;Bn0B02900;Fp10B8;Md6700;Ln116900086D27;Sv263:0000000000000000000000��
                    // This format is currently unsupported
                    return false;
                }
                if (headerString.StartsWith("RICOH", StringComparison.OrdinalIgnoreCase))
                {
                    PushDirectory(new RicohMakernoteDirectory());
                    // Always in Motorola byte order
                    TiffReader.ProcessIfd(this, context.WithByteOrder(isMotorolaByteOrder: true).WithShiftedBaseOffset(makernoteOffset), ifdOffset: 8);
                }
                else if (headerBytes.StartsWith("PENTAX \0II"u8))
                {
                    PushDirectory(new PentaxType2MakernoteDirectory());
                    TiffReader.ProcessIfd(this, context.WithByteOrder(isMotorolaByteOrder: false).WithShiftedBaseOffset(makernoteOffset), ifdOffset: 10);
                }
            }
            else if (headerBytes.StartsWith("Apple iOS\0"u8))
            {
                PushDirectory(new AppleMakernoteDirectory());
                // Always in Motorola byte order
                TiffReader.ProcessIfd(this, context.WithByteOrder(isMotorolaByteOrder: true).WithShiftedBaseOffset(makernoteOffset), ifdOffset: 14);
            }
            else if (context.Reader.GetUInt16(makernoteOffset) == ReconyxHyperFireMakernoteDirectory.MakernoteVersion)
            {
                var directory = new ReconyxHyperFireMakernoteDirectory();
                Directories.Add(directory);
                ProcessReconyxHyperFireMakernote(directory, context.Reader.WithShiftedBaseOffset(makernoteOffset));
            }
            else if (headerString.StartsWith("RECONYXUF", StringComparison.OrdinalIgnoreCase))
            {
                var directory = new ReconyxUltraFireMakernoteDirectory();
                Directories.Add(directory);
                ProcessReconyxUltraFireMakernote(directory, context.Reader.WithShiftedBaseOffset(makernoteOffset));
            }
            else if (headerString.StartsWith("RECONYXH2", StringComparison.OrdinalIgnoreCase))
            {
                var directory = new ReconyxHyperFire2MakernoteDirectory();
                Directories.Add(directory);
                ProcessReconyxHyperFire2Makernote(directory, context.Reader.WithShiftedBaseOffset(makernoteOffset));
            }
            else if (string.Equals(cameraMake, "SAMSUNG", StringComparison.OrdinalIgnoreCase))
            {
                // Only handles Type2 notes correctly. Others aren't implemented, and it's complex to determine which ones to use
                PushDirectory(new SamsungType2MakernoteDirectory());
                TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset);
            }
            else if (string.Equals(cameraMake, "DJI", StringComparison.Ordinal))
            {
                PushDirectory(new DjiMakernoteDirectory());
                TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset);
            }
            else if (string.Equals(cameraMake, "FLIR Systems", StringComparison.Ordinal))
            {
                PushDirectory(new FlirMakernoteDirectory());
                TiffReader.ProcessIfd(this, context, ifdOffset: makernoteOffset);
            }
            else
            {
                // The makernote is not comprehended by this library.
                // If you are reading this and believe a particular camera's image should be processed, get in touch.
                return false;
            }

            return true;
        }

        private static bool HandlePrintIM(Directory directory, int tagId)
        {
            if (tagId == ExifDirectoryBase.TagPrintImageMatchingInfo)
                return true;

            if (tagId == 0x0E00)
            {
                // It's tempting to say that every tag with ID 0x0E00 is a PIM tag, but we can't be 100% sure.
                // Limit this to a specific set of directories.
                if (directory is CasioType2MakernoteDirectory or
                    KyoceraMakernoteDirectory or
                    NikonType2MakernoteDirectory or
                    OlympusMakernoteDirectory or
                    PanasonicMakernoteDirectory or
                    PentaxMakernoteDirectory or
                    RicohMakernoteDirectory or
                    SanyoMakernoteDirectory or
                    SonyType1MakernoteDirectory)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Process PrintIM IFD
        /// </summary>
        /// <remarks>
        /// Converted from Exiftool version 10.33 created by Phil Harvey
        /// http://www.sno.phy.queensu.ca/~phil/exiftool/
        /// lib\Image\ExifTool\PrintIM.pm
        /// </remarks>
        private static void ProcessPrintIM(PrintIMDirectory directory, int tagValueOffset, IndexedReader reader, int byteCount)
        {
            if (byteCount == 0)
            {
                directory.AddError("Empty PrintIM data");
                return;
            }

            if (byteCount <= 15)
            {
                directory.AddError("Bad PrintIM data");
                return;
            }

            var header = reader.GetString(tagValueOffset, 12, Encoding.UTF8);

            if (!header.StartsWith("PrintIM", StringComparison.Ordinal))
            {
                directory.AddError("Invalid PrintIM header");
                return;
            }

            var localReader = reader;
            // check size of PrintIM block
            var num = localReader.GetUInt16(tagValueOffset + 14);
            if (byteCount < 16 + num * 6)
            {
                // size is too big, maybe byte ordering is wrong
                localReader = reader.WithByteOrder(!reader.IsMotorolaByteOrder);
                num = localReader.GetUInt16(tagValueOffset + 14);
                if (byteCount < 16 + num * 6)
                {
                    directory.AddError("Bad PrintIM size");
                    return;
                }
            }

            directory.Set(PrintIMDirectory.TagPrintImVersion, header.Substring(8, 4));

            for (var n = 0; n < num; n++)
            {
                var pos = tagValueOffset + 16 + n * 6;
                var tag = localReader.GetUInt16(pos);
                var val = localReader.GetUInt32(pos + 2);

                directory.Set(tag, val);
            }
        }

        private static void ProcessKodakMakernote(KodakMakernoteDirectory directory, int tagValueOffset, IndexedReader reader)
        {
            // Kodak's makernote is not in IFD format. It has values at fixed offsets.
            var dataOffset = tagValueOffset + 8;
            try
            {
#pragma warning disable format
                directory.Set(KodakMakernoteDirectory.TagKodakModel,           reader.GetString(dataOffset, 8, Encoding.UTF8));
                directory.Set(KodakMakernoteDirectory.TagQuality,              reader.GetByte(dataOffset + 9));
                directory.Set(KodakMakernoteDirectory.TagBurstMode,            reader.GetByte(dataOffset + 10));
                directory.Set(KodakMakernoteDirectory.TagImageWidth,           reader.GetUInt16(dataOffset + 12));
                directory.Set(KodakMakernoteDirectory.TagImageHeight,          reader.GetUInt16(dataOffset + 14));
                directory.Set(KodakMakernoteDirectory.TagYearCreated,          reader.GetUInt16(dataOffset + 16));
                directory.Set(KodakMakernoteDirectory.TagMonthDayCreated,      reader.GetBytes(dataOffset + 18, 2));
                directory.Set(KodakMakernoteDirectory.TagTimeCreated,          reader.GetBytes(dataOffset + 20, 4));
                directory.Set(KodakMakernoteDirectory.TagBurstMode2,           reader.GetUInt16(dataOffset + 24));
                directory.Set(KodakMakernoteDirectory.TagShutterMode,          reader.GetByte(dataOffset + 27));
                directory.Set(KodakMakernoteDirectory.TagMeteringMode,         reader.GetByte(dataOffset + 28));
                directory.Set(KodakMakernoteDirectory.TagSequenceNumber,       reader.GetByte(dataOffset + 29));
                directory.Set(KodakMakernoteDirectory.TagFNumber,              reader.GetUInt16(dataOffset + 30));
                directory.Set(KodakMakernoteDirectory.TagExposureTime,         reader.GetUInt32(dataOffset + 32));
                directory.Set(KodakMakernoteDirectory.TagExposureCompensation, reader.GetInt16(dataOffset + 36));
                directory.Set(KodakMakernoteDirectory.TagFocusMode,            reader.GetByte(dataOffset + 56));
                directory.Set(KodakMakernoteDirectory.TagWhiteBalance,         reader.GetByte(dataOffset + 64));
                directory.Set(KodakMakernoteDirectory.TagFlashMode,            reader.GetByte(dataOffset + 92));
                directory.Set(KodakMakernoteDirectory.TagFlashFired,           reader.GetByte(dataOffset + 93));
                directory.Set(KodakMakernoteDirectory.TagIsoSetting,           reader.GetUInt16(dataOffset + 94));
                directory.Set(KodakMakernoteDirectory.TagIso,                  reader.GetUInt16(dataOffset + 96));
                directory.Set(KodakMakernoteDirectory.TagTotalZoom,            reader.GetUInt16(dataOffset + 98));
                directory.Set(KodakMakernoteDirectory.TagDateTimeStamp,        reader.GetUInt16(dataOffset + 100));
                directory.Set(KodakMakernoteDirectory.TagColorMode,            reader.GetUInt16(dataOffset + 102));
                directory.Set(KodakMakernoteDirectory.TagDigitalZoom,          reader.GetUInt16(dataOffset + 104));
                directory.Set(KodakMakernoteDirectory.TagSharpness,            reader.GetSByte(dataOffset + 107));
#pragma warning restore format
            }
            catch (IOException ex)
            {
                directory.AddError("Error processing Kodak makernote data: " + ex.Message);
            }
        }

        private static void ProcessReconyxHyperFireMakernote(ReconyxHyperFireMakernoteDirectory directory, IndexedReader reader)
        {
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagMakernoteVersion, reader.GetUInt16(0));

            // revision and build are reversed from .NET ordering
            ushort major = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagFirmwareVersion);
            ushort minor = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagFirmwareVersion + 2);
            ushort revision = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagFirmwareVersion + 4);
            string buildYear = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagFirmwareVersion + 6).ToString("x4");
            string buildDate = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagFirmwareVersion + 8).ToString("x4");
            string buildYearAndDate = buildYear + buildDate;
            if (int.TryParse(buildYear + buildDate, out int build))
            {
                directory.Set(ReconyxHyperFireMakernoteDirectory.TagFirmwareVersion, new Version(major, minor, revision, build));
            }
            else
            {
                directory.Set(ReconyxHyperFireMakernoteDirectory.TagFirmwareVersion, new Version(major, minor, revision));
                directory.AddError("Error processing Reconyx HyperFire makernote data: build '" + buildYearAndDate + "' is not in the expected format and will be omitted from Firmware Version.");
            }

            directory.Set(ReconyxHyperFireMakernoteDirectory.TagTriggerMode, new string((char)reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagTriggerMode), 1));
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagSequence,
                          new[]
                          {
                              reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagSequence),
                              reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagSequence + 2)
                          });

            uint eventNumberHigh = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagEventNumber);
            uint eventNumberLow = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagEventNumber + 2);
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagEventNumber, (eventNumberHigh << 16) + eventNumberLow);

            ushort seconds = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagDateTimeOriginal);
            ushort minutes = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagDateTimeOriginal + 2);
            ushort hour = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagDateTimeOriginal + 4);
            ushort month = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagDateTimeOriginal + 6);
            ushort day = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagDateTimeOriginal + 8);
            ushort year = reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagDateTimeOriginal + 10);
            if (seconds < 60 &&
                minutes < 60 &&
                hour < 24 &&
                month is >= 1 and < 13 &&
                day is >= 1 and < 32 &&
                year >= DateTime.MinValue.Year && year <= DateTime.MaxValue.Year)
            {
                directory.Set(ReconyxHyperFireMakernoteDirectory.TagDateTimeOriginal, new DateTime(year, month, day, hour, minutes, seconds, DateTimeKind.Unspecified));
            }
            else
            {
                directory.AddError("Error processing Reconyx HyperFire makernote data: Date/Time Original " + year + "-" + month + "-" + day + " " + hour + ":" + minutes + ":" + seconds + " is not a valid date/time.");
            }

            directory.Set(ReconyxHyperFireMakernoteDirectory.TagMoonPhase, reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagMoonPhase));
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagAmbientTemperatureFahrenheit, reader.GetInt16(ReconyxHyperFireMakernoteDirectory.TagAmbientTemperatureFahrenheit));
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagAmbientTemperature, reader.GetInt16(ReconyxHyperFireMakernoteDirectory.TagAmbientTemperature));
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagSerialNumber, reader.GetString(ReconyxHyperFireMakernoteDirectory.TagSerialNumber, 28, Encoding.Unicode));
            // two unread bytes: the serial number's terminating null

            directory.Set(ReconyxHyperFireMakernoteDirectory.TagContrast, reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagContrast));
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagBrightness, reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagBrightness));
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagSharpness, reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagSharpness));
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagSaturation, reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagSaturation));
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagInfraredIlluminator, reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagInfraredIlluminator));
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagMotionSensitivity, reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagMotionSensitivity));
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagBatteryVoltage, reader.GetUInt16(ReconyxHyperFireMakernoteDirectory.TagBatteryVoltage) / 1000.0);
            directory.Set(ReconyxHyperFireMakernoteDirectory.TagUserLabel, reader.GetNullTerminatedString(ReconyxHyperFireMakernoteDirectory.TagUserLabel, 44));
        }

        private static void ProcessReconyxUltraFireMakernote(ReconyxUltraFireMakernoteDirectory directory, IndexedReader reader)
        {
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagLabel, reader.GetString(0, 9, Encoding.UTF8));
            uint makernoteId = ByteConvert.FromBigEndianToNative(reader.GetUInt32(ReconyxUltraFireMakernoteDirectory.TagMakernoteId));
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagMakernoteId, makernoteId);
            if (makernoteId != ReconyxUltraFireMakernoteDirectory.MakernoteId)
            {
                directory.AddError("Error processing Reconyx UltraFire makernote data: unknown Makernote ID 0x" + makernoteId.ToString("x8"));
                return;
            }
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagMakernoteSize, ByteConvert.FromBigEndianToNative(reader.GetUInt32(ReconyxUltraFireMakernoteDirectory.TagMakernoteSize)));
            uint makernotePublicId = ByteConvert.FromBigEndianToNative(reader.GetUInt32(ReconyxUltraFireMakernoteDirectory.TagMakernotePublicId));
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagMakernotePublicId, makernotePublicId);
            if (makernotePublicId != ReconyxUltraFireMakernoteDirectory.MakernotePublicId)
            {
                directory.AddError("Error processing Reconyx UltraFire makernote data: unknown Makernote Public ID 0x" + makernotePublicId.ToString("x8"));
                return;
            }
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagMakernotePublicSize, ByteConvert.FromBigEndianToNative(reader.GetUInt16(ReconyxUltraFireMakernoteDirectory.TagMakernotePublicSize)));

            directory.Set(ReconyxUltraFireMakernoteDirectory.TagCameraVersion, ProcessReconyxUltraFireVersion(ReconyxUltraFireMakernoteDirectory.TagCameraVersion, reader));
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagUibVersion, ProcessReconyxUltraFireVersion(ReconyxUltraFireMakernoteDirectory.TagUibVersion, reader));
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagBtlVersion, ProcessReconyxUltraFireVersion(ReconyxUltraFireMakernoteDirectory.TagBtlVersion, reader));
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagPexVersion, ProcessReconyxUltraFireVersion(ReconyxUltraFireMakernoteDirectory.TagPexVersion, reader));

            directory.Set(ReconyxUltraFireMakernoteDirectory.TagEventType, reader.GetString(ReconyxUltraFireMakernoteDirectory.TagEventType, 1, Encoding.UTF8));
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagSequence,
                          new[]
                          {
                              reader.GetByte(ReconyxUltraFireMakernoteDirectory.TagSequence),
                              reader.GetByte(ReconyxUltraFireMakernoteDirectory.TagSequence + 1)
                          });
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagEventNumber, ByteConvert.FromBigEndianToNative(reader.GetUInt32(ReconyxUltraFireMakernoteDirectory.TagEventNumber)));

            byte seconds = reader.GetByte(ReconyxUltraFireMakernoteDirectory.TagDateTimeOriginal);
            byte minutes = reader.GetByte(ReconyxUltraFireMakernoteDirectory.TagDateTimeOriginal + 1);
            byte hour = reader.GetByte(ReconyxUltraFireMakernoteDirectory.TagDateTimeOriginal + 2);
            byte day = reader.GetByte(ReconyxUltraFireMakernoteDirectory.TagDateTimeOriginal + 3);
            byte month = reader.GetByte(ReconyxUltraFireMakernoteDirectory.TagDateTimeOriginal + 4);
            ushort year = ByteConvert.FromBigEndianToNative(reader.GetUInt16(ReconyxUltraFireMakernoteDirectory.TagDateTimeOriginal + 5));
            if (seconds < 60 &&
                minutes < 60 &&
                hour < 24 &&
                month is >= 1 and < 13 &&
                day is >= 1 and < 32 &&
                year >= DateTime.MinValue.Year && year <= DateTime.MaxValue.Year)
            {
                directory.Set(ReconyxUltraFireMakernoteDirectory.TagDateTimeOriginal, new DateTime(year, month, day, hour, minutes, seconds, DateTimeKind.Unspecified));
            }
            else
            {
                directory.AddError("Error processing Reconyx UltraFire makernote data: Date/Time Original " + year + "-" + month + "-" + day + " " + hour + ":" + minutes + ":" + seconds + " is not a valid date/time.");
            }
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagDayOfWeek, reader.GetByte(ReconyxUltraFireMakernoteDirectory.TagDayOfWeek));

            directory.Set(ReconyxUltraFireMakernoteDirectory.TagMoonPhase, reader.GetByte(ReconyxUltraFireMakernoteDirectory.TagMoonPhase));
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagAmbientTemperatureFahrenheit, ByteConvert.FromBigEndianToNative(reader.GetInt16(ReconyxUltraFireMakernoteDirectory.TagAmbientTemperatureFahrenheit)));
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagAmbientTemperature, ByteConvert.FromBigEndianToNative(reader.GetInt16(ReconyxUltraFireMakernoteDirectory.TagAmbientTemperature)));

            directory.Set(ReconyxUltraFireMakernoteDirectory.TagFlash, reader.GetByte(ReconyxUltraFireMakernoteDirectory.TagFlash));
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagBatteryVoltage, ByteConvert.FromBigEndianToNative(reader.GetUInt16(ReconyxUltraFireMakernoteDirectory.TagBatteryVoltage)) / 1000.0);
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagSerialNumber, reader.GetString(ReconyxUltraFireMakernoteDirectory.TagSerialNumber, 14, Encoding.UTF8));
            // unread byte: the serial number's terminating null
            directory.Set(ReconyxUltraFireMakernoteDirectory.TagUserLabel, reader.GetNullTerminatedString(ReconyxUltraFireMakernoteDirectory.TagUserLabel, 20, Encoding.UTF8));
        }

        private static string ProcessReconyxUltraFireVersion(int versionOffset, IndexedReader reader)
        {
            string major = reader.GetByte(versionOffset).ToString();
            string minor = reader.GetByte(versionOffset + 1).ToString();
            string year = ByteConvert.FromBigEndianToNative(reader.GetUInt16(versionOffset + 2)).ToString("x4");
            string month = reader.GetByte(versionOffset + 4).ToString("x2");
            string day = reader.GetByte(versionOffset + 5).ToString("x2");
            string revision = reader.GetString(versionOffset + 6, 1, Encoding.UTF8);
            return major + "." + minor + "." + year + "." + month + "." + day + revision;
        }

        private static void ProcessReconyxHyperFire2Makernote(ReconyxHyperFire2MakernoteDirectory directory, IndexedReader reader)
        {
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagFileNumber, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagFileNumber));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagDirectoryNumber, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagDirectoryNumber));

            ushort major = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagFirmwareVersion);
            ushort minor = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagFirmwareVersion + 2);
            ushort revision = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagFirmwareVersion + 4);
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagFirmwareVersion, new Version(major, minor, revision));

            ushort year = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagFirmwareDate);
            ushort other = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagFirmwareDate + 2);
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagFirmwareDate, new DateTime(int.Parse(year.ToString("x4")), int.Parse((other >> 8).ToString("x2")), int.Parse((other & 0xFF).ToString("x2"))));

            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagTriggerMode,
                reader.GetNullTerminatedString(ReconyxHyperFire2MakernoteDirectory.TagTriggerMode, 2));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagSequence,
                new[]
                {
                    reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagSequence),
                    reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagSequence + 2)
                });

            ushort eventNumberHigh = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagEventNumber);
            ushort eventNumberLow = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagEventNumber + 2);
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagEventNumber, (eventNumberHigh << 16) + eventNumberLow);

            ushort seconds = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagDateTimeOriginal);
            ushort minutes = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagDateTimeOriginal + 2);
            ushort hour = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagDateTimeOriginal + 4);
            ushort month = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagDateTimeOriginal + 6);
            ushort day = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagDateTimeOriginal + 8);
            year = reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagDateTimeOriginal + 10);
            if (seconds < 60 &&
                minutes < 60 &&
                hour < 24 &&
                month is >= 1 and < 13 &&
                day is >= 1 and < 32 &&
                year >= DateTime.MinValue.Year && year <= DateTime.MaxValue.Year)
            {
                directory.Set(ReconyxHyperFire2MakernoteDirectory.TagDateTimeOriginal, new DateTime(year, month, day, hour, minutes, seconds, DateTimeKind.Unspecified));
            }
            else
            {
                directory.AddError("Error processing Reconyx HyperFire 2 makernote data: Date/Time Original " + year + "-" + month + "-" + day + " " + hour + ":" + minutes + ":" + seconds + " is not a valid date/time.");
            }
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagDayOfWeek, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagDayOfWeek));

            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagMoonPhase, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagMoonPhase));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagAmbientTemperatureFahrenheit, reader.GetInt16(ReconyxHyperFire2MakernoteDirectory.TagAmbientTemperatureFahrenheit));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagAmbientTemperatureCelcius, reader.GetInt16(ReconyxHyperFire2MakernoteDirectory.TagAmbientTemperatureCelcius));

            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagContrast, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagContrast));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagBrightness, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagBrightness));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagSharpness, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagSharpness));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagSaturation, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagSaturation));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagFlash, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagFlash));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagAmbientInfrared, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagAmbientInfrared));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagAmbientLight, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagAmbientLight));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagMotionSensitivity, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagMotionSensitivity));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagBatteryVoltage, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagBatteryVoltage) / 1000.0);
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagBatteryVoltageAvg, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagBatteryVoltageAvg) / 1000.0);
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagBatteryType, reader.GetUInt16(ReconyxHyperFire2MakernoteDirectory.TagBatteryType));
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagUserLabel, reader.GetNullTerminatedString(ReconyxHyperFire2MakernoteDirectory.TagUserLabel, 22));
            // two unread bytes: terminating null of unicode string
            directory.Set(ReconyxHyperFire2MakernoteDirectory.TagSerialNumber, reader.GetString(ReconyxHyperFire2MakernoteDirectory.TagSerialNumber, 28, Encoding.Unicode));
        }
    }
}
