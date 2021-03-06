﻿using Microsoft.Diagnostics.Runtime;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace HeapStringAnalyser
{
    // See https://github.com/Microsoft/clrmd/blob/master/Documentation/GettingStarted.md
    // and https://github.com/Microsoft/clrmd/blob/master/Documentation/ClrRuntime.md
    // and https://github.com/Microsoft/clrmd/blob/master/Documentation/WalkingTheHeap.md
    // A useful list of instructions for working with CLRMD, 
    // see http://blogs.msdn.com/b/kirillosenkov/archive/2014/07/05/get-most-duplicated-strings-from-a-heap-dump-using-clrmd.aspx
    class Program
    {
        // See "Some simple starter measurements" on http://codeblog.jonskeet.uk/2011/04/05/of-memory-and-strings/
        private static ulong HeaderSize = (ulong)(Environment.Is64BitProcess ? 26 : 14);

        static void Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(".NET Memory Dump Heap Analyser - created by Matt Warren - github.com/mattwarren\n");
            Console.ResetColor();

            if (args.Length < 1)
            {
                Console.WriteLine("Usage:\n  HeapStringAnalyser.exe <Dump File>\n");
                return;
            }

            var memoryDumpPath = args[0];
            if (File.Exists(memoryDumpPath) == false)
            {
                Console.WriteLine("{0} - does not exist!", memoryDumpPath);
                return;
            }

            using (DataTarget target = DataTarget.LoadCrashDump(memoryDumpPath))
            {
                ClrRuntime runtime = CreateRuntime(target);
                if (runtime == null)
                    return;

                var heap = runtime.GetHeap();

                var showGcHeapInfo = args.Any(a => a.ToLowerInvariant() == "--gcinfo" || a.ToLowerInvariant() == "-gcinfo");
                if (showGcHeapInfo)
                {
                    PrintMemoryRegionInfo(runtime);

                    PrintGCHeapInfo(runtime, heap);
                }
                
                ExamineProcessHeap(runtime, heap, showGcHeapInfo);
            }
        }

        private static void ExamineProcessHeap(ClrRuntime runtime, ClrHeap heap, bool showGcHeapInfo)
        {            
            if (!heap.CanWalkHeap)
            {
                Console.WriteLine("Cannot walk the heap!");
                return;
            }

            ulong totalStringObjectSize = 0, stringObjectCounter = 0, byteArraySize = 0;
            ulong asciiStringSize = 0, unicodeStringSize = 0, isoStringSize = 0; //, utf8StringSize = 0;
            ulong asciiStringCount = 0, unicodeStringCount = 0, isoStringCount = 0; //, utf8StringCount = 0;
            ulong compressedStringSize = 0, uncompressedStringSize = 0;
            foreach (var obj in heap.EnumerateObjectAddresses())
            {
                ClrType type = heap.GetObjectType(obj);
                // If heap corruption, continue past this object. Or if it's NOT a String we also ignore it
                if (type == null || type.IsString == false)
                    continue;

                stringObjectCounter++;
                var text = (string)type.GetValue(obj);
                var rawBytes = Encoding.Unicode.GetBytes(text);
                totalStringObjectSize += type.GetSize(obj);
                byteArraySize += (ulong)rawBytes.Length;

                VerifyStringObjectSize(runtime, type, obj, text);

                // Try each encoding in order, so we find the most-compact encoding that the text would fit in
                byte[] textAsBytes = null;
                if (IsASCII(text, out textAsBytes))
                {
                    asciiStringSize += (ulong)rawBytes.Length;
                    asciiStringCount++;

                    // ASCII is compressed as ISO-8859-1 (Latin-1) NOT ASCII
                    if (IsIsoLatin1(text, out textAsBytes))
                        compressedStringSize += (ulong)textAsBytes.Length;
                    else
                        Console.WriteLine("ERROR: \"{0}\" is ASCII but can't be encoded as ISO-8859-1 (Latin-1)", text);
                }
                // From http://stackoverflow.com/questions/7048745/what-is-the-difference-between-utf-8-and-iso-8859-1
                // "ISO 8859-1 is a single-byte encoding that can represent the first 256 Unicode characters"
                else if (IsIsoLatin1(text, out textAsBytes))
                {
                    isoStringSize += (ulong)rawBytes.Length;
                    isoStringCount++;
                    compressedStringSize += (ulong)textAsBytes.Length;
                }
                // UTF-8 and UTF-16 can both support the same range of text/character values ("Code Points"), they just store it in different ways
                // From http://stackoverflow.com/questions/4655250/difference-between-utf-8-and-utf-16/4655335#4655335
                // "Both UTF-8 and UTF-16 are variable length (multi-byte) encodings.
                // However, in UTF-8 a character may occupy a minimum of 8 bits, while in UTF-16 character length starts with 16 bits."
                //else if (IsUTF8(text, out textAsBytes))
                //{
                //    utf8StringSize += (ulong)rawBytes.Length;
                //    utf8StringCount++;
                //    compressedStringSize += (ulong)textAsBytes.Length;
                //}
                else
                {
                    unicodeStringSize += (ulong)rawBytes.Length;
                    unicodeStringCount++;
                    uncompressedStringSize += (ulong)rawBytes.Length;
                }
            }

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("\n\"System.String\" memory usage info");
            Console.ResetColor();
            Console.WriteLine("Overall {0:N0} \"System.String\" objects take up {1:N0} bytes ({2:N2} MB)",
                                stringObjectCounter, totalStringObjectSize, totalStringObjectSize / 1024.0 / 1024.0);
            Console.WriteLine("Of this underlying byte arrays (as Unicode) take up {0:N0} bytes ({1:N2} MB)",
                                byteArraySize, byteArraySize / 1024.0 / 1024.0);
            Console.WriteLine("Remaining data (object headers, other fields, etc) are {0:N0} bytes ({1:N2} MB), at {2:0.##} bytes per object\n",
                                totalStringObjectSize - byteArraySize,
                                (totalStringObjectSize - byteArraySize) / 1024.0 / 1024.0,
                                (totalStringObjectSize - byteArraySize) / (double)stringObjectCounter);

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("Actual Encoding that the \"System.String\" could be stored as (with corresponding data size)");
            Console.ResetColor();
            Console.WriteLine("  {0,15:N0} bytes ({1,8:N0} strings) as ASCII", asciiStringSize, asciiStringCount);
            Console.WriteLine("  {0,15:N0} bytes ({1,8:N0} strings) as ISO-8859-1 (Latin-1)", isoStringSize, isoStringCount);
            //Console.WriteLine("  {0,15:N0} bytes ({1,8:N0} strings) are UTF-8", utf8StringSize, utf8StringCount);
            Console.WriteLine("  {0,15:N0} bytes ({1,8:N0} strings) as Unicode", unicodeStringSize, unicodeStringCount);
            Console.WriteLine("Total: {0:N0} bytes (expected: {1:N0}{2})\n",
                                asciiStringSize + isoStringSize + unicodeStringSize, byteArraySize,
                                (asciiStringSize + isoStringSize + unicodeStringSize != byteArraySize) ? " - ERROR" : "");

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("Compression Summary:");
            Console.ResetColor();
            Console.WriteLine("  {0,15:N0} bytes Compressed (to ISO-8859-1 (Latin-1))", compressedStringSize);
            Console.WriteLine("  {0,15:N0} bytes Uncompressed (as Unicode)", uncompressedStringSize);
            Console.WriteLine("  {0,15:N0} bytes EXTRA to enable compression (1-byte field, per \"System.String\" object)", stringObjectCounter);
            var totalBytesUsed = compressedStringSize + uncompressedStringSize + stringObjectCounter;
            var totalBytesSaved = byteArraySize - totalBytesUsed;
            Console.WriteLine("\nTotal Usage:  {0:N0} bytes ({1:N2} MB), compared to {2:N0} ({3:N2} MB) before compression",
                                totalBytesUsed, totalBytesUsed / 1024.0 / 1024.0,
                                byteArraySize, byteArraySize / 1024.0 / 1024.0);
            Console.WriteLine("Total Saving: {0:N0} bytes ({1:N2} MB)\n", totalBytesSaved, totalBytesSaved / 1024.0 / 1024.0);            
        }

        // By default the encoder just replaces the invalid characters, so force it to throw an exception
        private static Encoding asciiEncoder = Encoding.GetEncoding(Encoding.ASCII.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        private static Encoding isoLatin1Encoder = Encoding.GetEncoding("ISO-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        private static bool IsASCII(string text, out byte[] textAsBytes)
        {
            var unicodeBytes = Encoding.Unicode.GetBytes(text);
            try
            {
                textAsBytes = Encoding.Convert(Encoding.Unicode, asciiEncoder, unicodeBytes);
                return true;
            }
            catch (EncoderFallbackException /*efEx*/)
            {
                textAsBytes = null;
                return false;
            }
        }

        private static bool IsIsoLatin1(string text, out byte[] textAsBytes)
        {
            var unicodeBytes = Encoding.Unicode.GetBytes(text);
            try
            {
                textAsBytes = Encoding.Convert(Encoding.Unicode, isoLatin1Encoder, unicodeBytes);
                return true;
            }
            catch (EncoderFallbackException /*efEx*/)
            {
                textAsBytes = null;
                return false;
            }
        }

        private static bool IsUTF8(string text, out byte[] textAsBytes)
        {
            var unicodeBytes = Encoding.Unicode.GetBytes(text);
            try
            {
                textAsBytes = Encoding.Convert(Encoding.Unicode, Encoding.UTF8, unicodeBytes);
                return true;
            }
            catch (EncoderFallbackException /*efEx*/)
            {
                textAsBytes = null;
                return false;
            }
        }

        private static ClrRuntime CreateRuntime(DataTarget target)
        {
            string dacLocation = null;
            foreach (ClrInfo version in target.ClrVersions)
            {
                Console.WriteLine("Found CLR Version: " + version.Version.ToString());
                dacLocation = LoadCorrectDacForMemoryDump(version);
            }

            var runtimeInfo = target.ClrVersions[0]; // just using the first runtime
            ClrRuntime runtime = null;
            try
            {
                if (string.IsNullOrEmpty(dacLocation))
                {
                    Console.WriteLine(dacLocation);
                    runtime = runtimeInfo.CreateRuntime();
                }
                else
                {
                    runtime = runtimeInfo.CreateRuntime(dacLocation);
                }
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine("\n" + ex);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nEnsure that this program is compliled for the same architecture as the memory dump (i.e. 32-bit or 64-bit)");
                Console.WriteLine(String.Format(".NET Memory Dump Heap Analyser is compiled as {0}-bit\n", Environment.Is64BitProcess ? "64" : "32"));
                Console.ResetColor();
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine("\n" + ex);
                Console.WriteLine("\nUnable to process the Memory Dump file!?");
                return null;
            }

            return runtime;
        }

        private static string LoadCorrectDacForMemoryDump(ClrInfo version)
        {
            // First try the main location, i.e.:
            // C:\Windows\Microsoft.NET\Framework\v4.0.30319\mscordacwks.dll
            if (version.LocalMatchingDac != null && File.Exists(version.LocalMatchingDac))
            {
                Console.WriteLine("\nDac already exists on the local machine at:\n{0}", version.LocalMatchingDac);
                return version.LocalMatchingDac;
            }

            // Location: <TEMP>\symbols\mscordacwks_amd64_amd64_4.0.30319.18444.dll\52717f9a96b000\mscordacwks_amd64_amd64_4.0.30319.18444.dll
            ModuleInfo dacInfo = version.DacInfo;
            var dacLocation = string.Format(@"{0}symbols\{1}\{2:x}{3:x}\{4}",
                                            Path.GetTempPath(), 
                                            dacInfo.FileName, 
                                            dacInfo.TimeStamp, 
                                            dacInfo.FileSize,
                                            dacInfo.FileName);

            if (File.Exists(dacLocation))
            {
                Console.WriteLine("\nDac {0} already exists in the local cache at:\n{1}", dacInfo.FileName, dacLocation);
                return dacLocation;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\nUnable to find copy of the dac ({0}) on the local machine.", dacInfo);
                Console.WriteLine("Expected location:\n" + dacLocation);
                Console.WriteLine("\nIt will now be downloaded from the Microsoft Symbol Server.");
                Console.WriteLine("Press <ENTER> if you are okay with this, if not you can just type Ctrl-C to exit");
                Console.ResetColor();
                Console.ReadLine();

                string downloadLocation = version.TryDownloadDac(new SymbolNotification());
                Console.WriteLine("Downloaded a copy of the dac to:\n" + downloadLocation);
                return downloadLocation;
            }
        }

        private static void PrintMemoryRegionInfo(ClrRuntime runtime)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("\nMemory Region Information");
            Console.ResetColor();
            var seperator = "------------------------------------------------";
            Console.WriteLine(seperator);
            Console.WriteLine("{0,24} {1,4} {2,15}", "Type", "Count", "Total Size (MB)");
            Console.WriteLine(seperator);
            foreach (var region in (from r in runtime.EnumerateMemoryRegions()
                                    //where r.Type != ClrMemoryRegionType.ReservedGCSegment
                                    group r by r.Type into g
                                    let total = g.Sum(p => (uint)p.Size)
                                    orderby total descending
                                    select new
                                    {
                                        TotalSize = total,
                                        Count = g.Count(),
                                        Type = g.Key
                                    }))
            {
                Console.WriteLine("{0,24} {1,5} {2,15:N2}", region.Type.ToString(), region.Count, region.TotalSize / 1024.0 / 1024.0);
            }
            Console.WriteLine(seperator);
        }

        private static void PrintGCHeapInfo(ClrRuntime runtime, ClrHeap heap)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("\nGC Heap Information - {0}", runtime.ServerGC ? "Server" : "Workstation");
            Console.ResetColor();

            var seperator = "-----------------------------------------------------------";
            var heapSegmentInfo = from seg in heap.Segments
                               group seg by seg.ProcessorAffinity into g
                               orderby g.Key
                               select new
                               {
                                   Heap = g.Key,
                                   Size = g.Sum(p => (uint)p.Length)
                               };
            foreach (var item in heapSegmentInfo)
            {
                Console.WriteLine(seperator);
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine("Heap {0,2}: {1,12:N0} bytes ({2:N2} MB) in use", 
                                  item.Heap, item.Size, item.Size / 1024.0 / 1024.0);
                Console.ResetColor();
                Console.WriteLine(seperator);
                Console.WriteLine("{0,12} {1,12} {2,14} {3,14}", "Type", "Size (MB)", "Committed (MB)", "Reserved (MB)");
                Console.WriteLine(seperator);
                var heapSegments = heap.Segments.Where(s => s.ProcessorAffinity == item.Heap)
                                                .OrderBy(s => s.IsEphemeral ? 1 : 0 + (s.IsLarge ? 3 : 2));
                foreach (ClrSegment segment in heapSegments)
                {
                    string type;
                    if (segment.IsEphemeral)
                        type = "Ephemeral";
                    else if (segment.IsLarge)
                        type = "Large";
                    else
                        type = "Gen2";

                    Console.WriteLine("{0,12} {1,12:N2} {2,14:N2} {3,14:N2}", 
                                      type,                                
                                      (segment.End - segment.Start) / 1024.0 / 1024.0, // This is the same as segment.Length
                                      (segment.CommittedEnd - segment.Start) / 1024.0 / 1024.0,
                                      (segment.ReservedEnd - segment.Start) / 1024.0 / 1024.0);
                }
            }
            Console.WriteLine(seperator);
            Console.WriteLine("Total (across all heaps): {0:N0} bytes ({1:N2} MB)", 
                              heap.Segments.Sum(s => (long)s.Length), 
                              heap.Segments.Sum(s => (long)s.Length) / 1024.0 / 1024.0);
            Console.WriteLine(seperator);
        }

        private static void VerifyStringObjectSize(ClrRuntime runtime, ClrType type, ulong obj, string text)
        {
            var objSize = type.GetSize(obj);
            var objAsHex = obj.ToString("x");
            var rawBytes = Encoding.Unicode.GetBytes(text);

            if (runtime.ClrInfo.Version.Major == 2)
            {
                // This only works in .NET 2.0, the "m_array_Length" field was removed in .NET 4.0
                var arrayLength = (int)type.GetFieldByName("m_arrayLength").GetValue(obj);
                var stringLength = (int)type.GetFieldByName("m_stringLength").GetValue(obj);
                
                var calculatedSize = (((ulong)arrayLength - 1) * 2) + HeaderSize;
                if (objSize != calculatedSize)
                {
                    Console.WriteLine("Object Size Mismatch: arrayLength: {0,4}, stringLength: {1,4}, Object Size: {2,4}, Object: {3} -> \n\"{4}\"",
                                      arrayLength, stringLength, objSize, objAsHex, text);
                }
            }
            else
            {
                // In .NET 4.0 we can do a more normal check, i.e. ("object size" - "raw byte array length") should equal the expected header size
                var theRest = objSize - (ulong)rawBytes.Length;
                if (theRest != HeaderSize)
                {
                    Console.WriteLine("Object Size Mismatch: Raw Bytes Length: {0,4}, Object Size: {1,4}, Object: {2} -> \n\"{3}\"",
                                      rawBytes.Length, objSize, objAsHex, text);
                }
            }
        }
    }

    class SymbolNotification : ISymbolNotification
    {
        public void DecompressionComplete(string localPath)
        {
            Console.WriteLine("DecompressionComplete: " + (localPath ?? "<NULL>"));
        }

        public void DownloadComplete(string localPath, bool requiresDecompression)
        {
            Console.WriteLine("DecompressionComplete: " + (localPath ?? "<NULL>"));
        }

        public void DownloadProgress(int bytesDownloaded)
        {
            Console.WriteLine("DownloadProgress: bytesDownloaded = " + bytesDownloaded);
        }

        public void FoundSymbolInCache(string localPath)
        {
            Console.WriteLine("FoundSymbolInCache: " + (localPath ?? "<NULL>"));
        }

        public void FoundSymbolOnPath(string url)
        {
            Console.WriteLine("FoundSymbolOnPath: " + (url ?? "<NULL>"));
        }

        public void ProbeFailed(string url)
        {
            Console.WriteLine("ProbeFailed: " + (url ?? "<NULL>"));
        }
    }
}
