using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>Minimal reader for the Census polygon shapefiles (.shp + .dbf): every record's attributes and rings (lat, lon).</summary>
internal static class Shapefile
{
    public sealed record Record(Dictionary<string, string> Attributes, List<double[]> Rings);

    public static List<Record> Read(string shpPath)
    {
        var attributes = ReadDbf(Path.ChangeExtension(shpPath, ".dbf"));
        var records = new List<Record>();
        using var shp = new BinaryReader(File.OpenRead(shpPath));
        shp.BaseStream.Seek(100, SeekOrigin.Begin);
        var index = 0;
        while (shp.BaseStream.Position < shp.BaseStream.Length)
        {
            ReadInt32BigEndian(shp); // record number
            var contentWords = ReadInt32BigEndian(shp);
            var contentStart = shp.BaseStream.Position;
            var shapeType = shp.ReadInt32();
            var attrs = attributes[index++];
            if (shapeType == 5)
            {
                shp.ReadBytes(32); // bounding box
                var partCount = shp.ReadInt32();
                var pointCount = shp.ReadInt32();
                var parts = new int[partCount];
                for (var i = 0; i < partCount; i++)
                {
                    parts[i] = shp.ReadInt32();
                }

                var xs = new double[pointCount];
                var ys = new double[pointCount];
                for (var i = 0; i < pointCount; i++)
                {
                    xs[i] = shp.ReadDouble();
                    ys[i] = shp.ReadDouble();
                }

                var rings = new List<double[]>();
                for (var p = 0; p < partCount; p++)
                {
                    var start = parts[p];
                    var end = p + 1 < partCount ? parts[p + 1] : pointCount;
                    var ring = new double[(end - start) * 2];
                    for (var i = start; i < end; i++)
                    {
                        ring[(i - start) * 2] = ys[i];
                        ring[((i - start) * 2) + 1] = xs[i];
                    }

                    rings.Add(ring);
                }

                records.Add(new Record(attrs, rings));
            }

            shp.BaseStream.Seek(contentStart + (contentWords * 2L), SeekOrigin.Begin);
        }

        return records;
    }

    private static int ReadInt32BigEndian(BinaryReader reader)
    {
        var b = reader.ReadBytes(4);
        return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }

    private static List<Dictionary<string, string>> ReadDbf(string path)
    {
        using var dbf = new BinaryReader(File.OpenRead(path));
        dbf.ReadBytes(4);
        var recordCount = dbf.ReadInt32();
        var headerLength = dbf.ReadInt16();
        var recordLength = dbf.ReadInt16();
        dbf.BaseStream.Seek(32, SeekOrigin.Begin);
        var fields = new List<(string Name, int Length)>();
        while (dbf.PeekChar() != 0x0D)
        {
            var descriptor = dbf.ReadBytes(32);
            fields.Add((Encoding.ASCII.GetString(descriptor, 0, 11).TrimEnd('\0'), descriptor[16]));
        }

        var rows = new List<Dictionary<string, string>>(recordCount);
        dbf.BaseStream.Seek(headerLength, SeekOrigin.Begin);
        for (var r = 0; r < recordCount; r++)
        {
            var raw = dbf.ReadBytes(recordLength);
            var row = new Dictionary<string, string>();
            var offset = 1;
            foreach (var (name, length) in fields)
            {
                row[name] = Encoding.UTF8.GetString(raw, offset, length).Trim();
                offset += length;
            }

            rows.Add(row);
        }

        return rows;
    }
}
