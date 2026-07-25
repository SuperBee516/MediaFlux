using System.Buffers.Binary;
using System.Text;

namespace MediaFlux.Services
{
    /// <summary>
    /// Reads the playback duration described by a DVD title-set IFO. VOB packet
    /// timestamps are not a reliable duration source: their 33-bit MPEG clock can
    /// wrap, reset, or contain authoring discontinuities between program segments.
    /// </summary>
    internal static class DvdIfoDurationReader
    {
        private const int SectorSize = 2048;
        private const int VtsPgcitiSectorOffset = 0xCC;
        private const int PgcHeaderSize = 236;
        private const int CellPlaybackSize = 24;
        private const int CellPositionSize = 4;
        private const int MaxIfoBytes = 64 * 1024 * 1024;

        public static bool TryReadTitleSetDuration(
            string videoTsFolder,
            string titleSetId,
            out double durationSeconds,
            out string error)
        {
            durationSeconds = 0;
            error = "";

            string expectedName = $"{titleSetId}_0.IFO";
            string? ifoPath;
            try
            {
                ifoPath = Directory.EnumerateFiles(
                        videoTsFolder,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(path => Path.GetFileName(path).Equals(
                        expectedName,
                        StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                error = $"The DVD control file could not be located: {ex.Message}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(ifoPath))
            {
                error = $"{expectedName} was not found.";
                return false;
            }

            return TryReadDuration(ifoPath, out durationSeconds, out error);
        }

        internal static bool TryReadDuration(
            string ifoPath,
            out double durationSeconds,
            out string error)
        {
            durationSeconds = 0;
            error = "";

            byte[] data;
            try
            {
                var file = new FileInfo(ifoPath);
                if (!file.Exists)
                {
                    error = "The DVD control file does not exist.";
                    return false;
                }
                if (file.Length <= VtsPgcitiSectorOffset + sizeof(uint) ||
                    file.Length > MaxIfoBytes)
                {
                    error = "The DVD control file has an invalid size.";
                    return false;
                }

                data = File.ReadAllBytes(ifoPath);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = $"The DVD control file could not be read: {ex.Message}";
                return false;
            }

            if (data.Length < 12 ||
                !Encoding.ASCII.GetString(data, 0, 12).Equals(
                    "DVDVIDEO-VTS",
                    StringComparison.Ordinal))
            {
                error = "The file is not a DVD video title-set control file.";
                return false;
            }

            uint tableSector = ReadUInt32(data, VtsPgcitiSectorOffset);
            long tableOffsetLong = (long)tableSector * SectorSize;
            if (tableOffsetLong < 0 || tableOffsetLong > data.Length - 8)
            {
                error = "The DVD program-chain table points outside the control file.";
                return false;
            }

            int tableOffset = (int)tableOffsetLong;
            int entryCount = ReadUInt16(data, tableOffset);
            uint lastTableByte = ReadUInt32(data, tableOffset + 4);
            if (entryCount <= 0 ||
                entryCount > 10_000 ||
                lastTableByte < 7 ||
                (long)tableOffset + lastTableByte >= data.Length ||
                (long)tableOffset + 8 + (entryCount * 8L) > data.Length)
            {
                error = "The DVD program-chain table is invalid.";
                return false;
            }

            var uniqueCells = new Dictionary<CellKey, double>();
            for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
            {
                int entryOffset = tableOffset + 8 + (entryIndex * 8);
                uint pgcRelativeOffset = ReadUInt32(data, entryOffset + 4);
                long pgcOffsetLong = (long)tableOffset + pgcRelativeOffset;
                if (pgcOffsetLong < 0 || pgcOffsetLong > data.Length - PgcHeaderSize)
                {
                    error = "A DVD program chain points outside the control file.";
                    return false;
                }

                int pgcOffset = (int)pgcOffsetLong;
                int cellCount = data[pgcOffset + 3];
                if (cellCount == 0)
                    continue;

                int playbackRelativeOffset = ReadUInt16(data, pgcOffset + 232);
                int positionRelativeOffset = ReadUInt16(data, pgcOffset + 234);
                long playbackEnd = (long)pgcOffset + playbackRelativeOffset +
                                   (cellCount * (long)CellPlaybackSize);
                long positionEnd = (long)pgcOffset + positionRelativeOffset +
                                   (cellCount * (long)CellPositionSize);
                if (playbackRelativeOffset < PgcHeaderSize ||
                    positionRelativeOffset < PgcHeaderSize ||
                    playbackEnd > data.Length ||
                    positionEnd > data.Length)
                {
                    error = "A DVD program chain has an invalid cell table.";
                    return false;
                }

                for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
                {
                    int playbackOffset =
                        pgcOffset + playbackRelativeOffset + (cellIndex * CellPlaybackSize);
                    int positionOffset =
                        pgcOffset + positionRelativeOffset + (cellIndex * CellPositionSize);
                    if (!TryReadDvdTime(
                            data,
                            playbackOffset + 4,
                            out double cellDuration))
                    {
                        error = "A DVD cell contains an invalid playback time.";
                        return false;
                    }

                    ushort vobId = ReadUInt16(data, positionOffset);
                    byte cellId = data[positionOffset + 3];
                    uint firstSector = ReadUInt32(data, playbackOffset + 8);
                    uint lastSector = ReadUInt32(data, playbackOffset + 20);
                    var key = new CellKey(vobId, cellId, firstSector, lastSector);

                    if (cellDuration > 0 &&
                        (!uniqueCells.TryGetValue(key, out double existing) ||
                         cellDuration > existing))
                    {
                        uniqueCells[key] = cellDuration;
                    }
                }
            }

            durationSeconds = uniqueCells.Values.Sum();
            if (durationSeconds <= 0 || !double.IsFinite(durationSeconds))
            {
                durationSeconds = 0;
                error = "The DVD control file contains no usable cell playback durations.";
                return false;
            }

            return true;
        }

        private static bool TryReadDvdTime(
            byte[] data,
            int offset,
            out double seconds)
        {
            seconds = 0;
            if (offset < 0 || offset > data.Length - 4)
                return false;

            if (!TryReadBcd(data[offset], out int hours) ||
                !TryReadBcd(data[offset + 1], out int minutes) ||
                !TryReadBcd(data[offset + 2], out int wholeSeconds) ||
                minutes > 59 ||
                wholeSeconds > 59)
            {
                return false;
            }

            byte frameValue = data[offset + 3];
            int frameRateCode = frameValue & 0xC0;
            if (!TryReadBcd((byte)(frameValue & 0x3F), out int frames))
                return false;

            double frameRate = frameRateCode switch
            {
                0x40 => 25d,
                0xC0 => 30_000d / 1_001d,
                0 when frames == 0 => 0,
                _ => -1
            };
            if (frameRate < 0 || (frameRate > 0 && frames >= Math.Ceiling(frameRate)))
                return false;

            seconds =
                (hours * 3600d) +
                (minutes * 60d) +
                wholeSeconds +
                (frameRate > 0 ? frames / frameRate : 0);
            return true;
        }

        private static bool TryReadBcd(byte value, out int result)
        {
            int high = value >> 4;
            int low = value & 0x0F;
            if (high > 9 || low > 9)
            {
                result = 0;
                return false;
            }

            result = (high * 10) + low;
            return true;
        }

        private static ushort ReadUInt16(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, sizeof(ushort)));

        private static uint ReadUInt32(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, sizeof(uint)));

        private readonly record struct CellKey(
            ushort VobId,
            byte CellId,
            uint FirstSector,
            uint LastSector);
    }
}
