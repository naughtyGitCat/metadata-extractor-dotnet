// Copyright (c) Drew Noakes and contributors. All Rights Reserved. Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using MetadataExtractor.Formats.Jpeg;

namespace MetadataExtractor.Formats.Exif
{
    /// <summary>Unit tests for <see cref="ExifReader"/>.</summary>
    /// <author>Drew Noakes https://drewnoakes.com</author>
    public sealed class ExifReaderTest
    {
        #region Helpers

        public static IList<Directory> ProcessSegmentBytes(string filePath, JpegSegmentType type)
        {
            var segment = new JpegSegment(type, TestDataUtil.GetBytes(filePath), 0);

            return new ExifReader().ReadJpegSegments([segment]).ToList();
        }

        public static T ProcessSegmentBytes<T>(string filePath, JpegSegmentType type) where T : Directory
        {
            return ProcessSegmentBytes(filePath, type).OfType<T>().First();
        }

        #endregion

        [Fact]
        public void ReadJpegSegmentsWithNullDataThrows()
        {
            // ReSharper disable once AssignNullToNotNullAttribute
            Assert.Throws<ArgumentNullException>(() => new ExifReader().ReadJpegSegments(null!));
        }

        [Fact]
        public void LoadFujifilmJpeg()
        {
            var directory = ProcessSegmentBytes<ExifSubIfdDirectory>("Data/withExif.jpg.app1", JpegSegmentType.App1);

            Assert.Equal("80", directory.GetDescription(ExifDirectoryBase.TagIsoEquivalent));
        }

        [Fact]
        public void ReadJpegSegmentWithNoExifData()
        {
            var badExifSegment = new JpegSegment(JpegSegmentType.App1, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], offset: 0);
            var directories = new ExifReader().ReadJpegSegments([badExifSegment]);
            Assert.Empty(directories);
        }

        [Fact]
        public void CrashRegressionTest()
        {
            // This image was created via a resize in ACDSee.
            // It seems to have a reference to an IFD starting outside the data segment.
            // I've noticed that ACDSee reports a Comment for this image, yet ExifReader doesn't report one.
            var directory = ProcessSegmentBytes<ExifSubIfdDirectory>("Data/crash01.jpg.app1", JpegSegmentType.App1);
            Assert.True(directory.TagCount > 0);
        }

        [Fact]
        public void DateTime()
        {
            var directory = ProcessSegmentBytes<ExifIfd0Directory>("Data/manuallyAddedThumbnail.jpg.app1", JpegSegmentType.App1);
            Assert.Equal("2002:11:27 18:00:35", directory.GetString(ExifDirectoryBase.TagDateTime));
        }

        [Fact]
        public void ThumbnailXResolution()
        {
            var directory = ProcessSegmentBytes<ExifThumbnailDirectory>("Data/manuallyAddedThumbnail.jpg.app1", JpegSegmentType.App1);
            var rational = directory.GetRational(ExifDirectoryBase.TagXResolution);
            Assert.Equal(72, rational.Numerator);
            Assert.Equal(1, rational.Denominator);
        }

        [Fact]
        public void ThumbnailYResolution()
        {
            var directory = ProcessSegmentBytes<ExifThumbnailDirectory>("Data/manuallyAddedThumbnail.jpg.app1", JpegSegmentType.App1);
            var rational = directory.GetRational(ExifDirectoryBase.TagYResolution);
            Assert.Equal(72, rational.Numerator);
            Assert.Equal(1, rational.Denominator);
        }

        [Fact]
        public void ThumbnailOffset()
        {
            var directory = ProcessSegmentBytes<ExifThumbnailDirectory>("Data/manuallyAddedThumbnail.jpg.app1", JpegSegmentType.App1);
            Assert.Equal(192, directory.GetInt32(ExifThumbnailDirectory.TagThumbnailOffset));
        }

        [Fact]
        public void ThumbnailLength()
        {
            var directory = ProcessSegmentBytes<ExifThumbnailDirectory>("Data/manuallyAddedThumbnail.jpg.app1", JpegSegmentType.App1);
            Assert.Equal(2970, directory.GetInt32(ExifThumbnailDirectory.TagThumbnailLength));
        }

        [Fact]
        public void ThumbnailCompression()
        {
            var directory = ProcessSegmentBytes<ExifThumbnailDirectory>("Data/manuallyAddedThumbnail.jpg.app1", JpegSegmentType.App1);
            // 6 means JPEG compression
            Assert.Equal(6, directory.GetInt32(ExifDirectoryBase.TagCompression));
        }

        [Fact]
        public void StackOverflowOnRevisitationOfSameDirectory()
        {
            // An error has been discovered in Exif data segments where a directory is referenced
            // repeatedly.  Thanks to Alistair Dickie for providing the sample data used in this
            // unit test.
            var directories = ProcessSegmentBytes("Data/recursiveDirectories.jpg.app1", JpegSegmentType.App1);

            // Mostly we're just happy at this point that we didn't get stuck in an infinite loop.
            Assert.Equal(5, directories.Count);
        }

        [Fact]
        public void DifferenceImageAndThumbnailOrientations()
        {
            // This metadata contains different orientations for the thumbnail and the main image.
            // These values used to be merged into a single directory, causing errors.
            // This unit test demonstrates correct behavior.
            var directories = ProcessSegmentBytes("Data/repeatedOrientationTagWithDifferentValues.jpg.app1", JpegSegmentType.App1).ToList();

            var ifd0Directory = directories.OfType<ExifIfd0Directory>().First();
            var thumbnailDirectory = directories.OfType<ExifThumbnailDirectory>().First();
            Assert.NotNull(ifd0Directory);
            Assert.NotNull(thumbnailDirectory);
            Assert.Equal(1, ifd0Directory.GetInt32(ExifDirectoryBase.TagOrientation));
            Assert.Equal(8, thumbnailDirectory.GetInt32(ExifDirectoryBase.TagOrientation));
        }

        /*
        public void testUncompressedYCbCrThumbnail() throws Exception
        {
            String fileName = "withUncompressedYCbCrThumbnail.jpg";
            String thumbnailFileName = "withUncompressedYCbCrThumbnail.bmp";
            Metadata metadata = new ExifReader(new File(fileName)).extract();
            ExifSubIFDDirectory directory = (ExifSubIFDDirectory)metadata.getOrCreateDirectory(ExifSubIFDDirectory.class);
            directory.writeThumbnail(thumbnailFileName);

            fileName = "withUncompressedYCbCrThumbnail2.jpg";
            thumbnailFileName = "withUncompressedYCbCrThumbnail2.bmp";
            metadata = new ExifReader(new File(fileName)).extract();
            directory = (ExifSubIFDDirectory)metadata.getOrCreateDirectory(ExifSubIFDDirectory.class);
            directory.writeThumbnail(thumbnailFileName);
            fileName = "withUncompressedYCbCrThumbnail3.jpg";
            thumbnailFileName = "withUncompressedYCbCrThumbnail3.bmp";
            metadata = new ExifReader(new File(fileName)).extract();
            directory = (ExifSubIFDDirectory)metadata.getOrCreateDirectory(ExifSubIFDDirectory.class);
            directory.writeThumbnail(thumbnailFileName);
            fileName = "withUncompressedYCbCrThumbnail4.jpg";
            thumbnailFileName = "withUncompressedYCbCrThumbnail4.bmp";
            metadata = new ExifReader(new File(fileName)).extract();
            directory = (ExifSubIFDDirectory)metadata.getOrCreateDirectory(ExifSubIFDDirectory.class);
            directory.writeThumbnail(thumbnailFileName);
        }

        public void testUncompressedRGBThumbnail() throws Exception
        {
            String fileName = "withUncompressedRGBThumbnail.jpg";
            String thumbnailFileName = "withUncompressedRGBThumbnail.bmp";
            Metadata metadata = new ExifReader(new File(fileName)).extract();
            ExifSubIFDDirectory directory = (ExifSubIFDDirectory)metadata.getOrCreateDirectory(ExifSubIFDDirectory.class);
            directory.writeThumbnail(thumbnailFileName);
        }
        */
    }
}
