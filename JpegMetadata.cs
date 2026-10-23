using System.Text;

static class JpegMetadata
{
    // Rebuilds a JPEG without its metadata segments (EXIF, XMP, IPTC, comments, thumbnails).
    // The compressed image data is copied byte for byte, so the picture itself is unchanged.
    // Returns null if the data isn't a JPEG that can be parsed.
    public static byte[]? Strip(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        using MemoryStream output = new();
        output.Write(data, 0, 2);
        int pos = 2;

        while (true)
        {
            // Markers may be preceded by any number of 0xFF fill bytes
            if (pos >= data.Length || data[pos] != 0xFF)
            {
                return null;
            }
            while (pos < data.Length && data[pos] == 0xFF)
            {
                pos++;
            }
            if (pos >= data.Length)
            {
                return null;
            }
            byte marker = data[pos++];

            // End of image: anything after it (e.g. extra preview images some cameras append) is dropped
            if (marker == 0xD9)
            {
                output.Write([0xFF, 0xD9]);
                return output.ToArray();
            }

            // Markers with no length or data
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                output.Write([0xFF, marker]);
                continue;
            }

            if (pos + 2 > data.Length)
            {
                return null;
            }
            int length = (data[pos] << 8) | data[pos + 1];
            if (length < 2 || pos + length > data.Length)
            {
                return null;
            }

            WriteSegmentIfKept(output, marker, data, pos, length);
            pos += length;

            // Start of scan: the compressed image data follows, running until the next real marker.
            // 0xFF bytes inside it are always followed by 0x00 (stuffing) or a restart marker.
            if (marker == 0xDA)
            {
                int scanStart = pos;
                while (pos + 1 < data.Length &&
                       !(data[pos] == 0xFF && data[pos + 1] != 0x00 && !(data[pos + 1] >= 0xD0 && data[pos + 1] <= 0xD7)))
                {
                    pos++;
                }
                if (pos + 1 >= data.Length)
                {
                    return null;
                }
                output.Write(data, scanStart, pos - scanStart);
            }
        }
    }

    // pos points at the segment's 2-byte length, which counts itself plus the payload
    static void WriteSegmentIfKept(MemoryStream output, byte marker, byte[] data, int pos, int length)
    {
        int payload = pos + 2;
        int payloadLength = length - 2;

        // Comment
        if (marker == 0xFE)
        {
            return;
        }

        // Anything other than APP0-APP15 is needed to decode the image (tables, frame and scan headers)
        if (marker < 0xE0 || marker > 0xEF)
        {
            output.Write([0xFF, marker]);
            output.Write(data, pos, length);
            return;
        }

        switch (marker)
        {
            // JFIF header: keep it, but without any embedded thumbnail
            case 0xE0 when HasSignature(data, payload, payloadLength, "JFIF\0") && payloadLength >= 14:
                output.Write([0xFF, 0xE0, 0x00, 16]);
                output.Write(data, payload, 12);
                output.Write([0x00, 0x00]);
                return;

            // Colour profile, which can be split across several segments
            case 0xE2 when HasSignature(data, payload, payloadLength, "ICC_PROFILE\0"):
            // Adobe colour transform flags, needed to decode some CMYK/RGB JPEGs correctly
            case 0xEE when HasSignature(data, payload, payloadLength, "Adobe"):
                output.Write([0xFF, marker]);
                output.Write(data, pos, length);
                return;
        }

        // Every other APPn segment is metadata: EXIF and XMP (APP1), IPTC/Photoshop (APP13), etc.
    }

    static bool HasSignature(byte[] data, int offset, int count, string signature)
    {
        byte[] expected = Encoding.ASCII.GetBytes(signature);
        return count >= expected.Length && data.AsSpan(offset, expected.Length).SequenceEqual(expected);
    }
}
