#nullable enable

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public static unsafe class PluginIndexer
{
    public static Metadata Read(string path)
    {
        // NativeAOT modules stay loaded until this indexing process exits.
        nint library = NativeLibrary.Load(path);
        nint resolver = NativeLibrary.GetExport(library, "TryGetApiExport");

        nint Find(string name)
        {
            nint result = 0;
            fixed (char* key = name)
            {
                int status = ((delegate* unmanaged[Cdecl]<char*, nint*, int>)resolver)(key, &result);
                if (status != 0 || result == 0)
                    throw new InvalidDataException("Missing Collapse export: " + name);
            }
            return result;
        }

        Version ReadVersion(string name)
        {
            int* data = ((delegate* unmanaged[Cdecl]<int*>)Find(name))();
            if (data == null)
                throw new InvalidDataException("Missing Collapse version: " + name);
            return new Version(data[0], data[1], data[2], data[3]);
        }

        Version standard = ReadVersion("GetPluginStandardVersion");
        if (standard != new Version(0, 1, 6, 2))
            throw new InvalidDataException("Unexpected Collapse API version: " + standard);

        nint unknown = ((delegate* unmanaged[Cdecl]<nint>)Find("GetPlugin"))();
        if (unknown == 0)
            throw new InvalidDataException("Collapse GetPlugin returned no interface.");
        nint plugin = 0;

        try
        {
            Guid interfaceId = new Guid("436f6c6c-6170-7365-727f-000000000010");
            nint* unknownTable = *(nint**)unknown;
            Marshal.ThrowExceptionForHR(
                ((delegate* unmanaged[MemberFunction]<nint, Guid*, nint*, int>)unknownTable[0])(unknown, &interfaceId, &plugin));
            if (plugin == 0)
                throw new InvalidDataException("Collapse returned a missing plugin interface.");

            nint* table = *(nint**)plugin;

            DateTime* created = null;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, DateTime**, int>)table[7])(plugin, &created));
            if (created == null)
                throw new InvalidDataException("Missing plugin creation date.");

            int presetCount = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, int*, int>)table[8])(plugin, &presetCount));
            if (presetCount != 2)
                throw new InvalidDataException("Expected the China and Japan presets.");

            nint updater = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, nint*, int>)table[15])(plugin, &updater));
            if (updater == 0)
                throw new InvalidDataException("Collapse plugin updater is missing.");
            Marshal.Release(updater);

            string? name = ReadString(plugin, table[4]);
            string? description = ReadString(plugin, table[5]);
            string? author = ReadString(plugin, table[6]);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(author))
                throw new InvalidDataException("Incomplete Collapse plugin metadata.");

            return new Metadata(ReadVersion("GetPluginVersion"), standard, name, description, author, *created, ReadString(plugin, table[9]),
                ReadUpdateSources(Find("GetPluginUpdateCdnList")));
        }
        finally
        {
            if (plugin != 0)
                Marshal.Release(plugin);
            Marshal.Release(unknown);
        }
    }

    private static string? ReadString(nint plugin, nint getter)
    {
        nint text = 0;
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[MemberFunction]<nint, nint*, int>)getter)(plugin, &text));
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private static string[] ReadUpdateSources(nint getUpdateSources)
    {
        int count = 0;
        nint* sources = null;
        ((delegate* unmanaged[Cdecl]<int*, nint**, void>)getUpdateSources)(&count, &sources);
        try
        {
            if (count != 2 || sources == null)
                throw new InvalidDataException("Expected GitHub Releases and ESA plugin update sources.");

            return
            [
                Marshal.PtrToStringUni(sources[0]) ?? throw new InvalidDataException("Missing GitHub Releases URL."),
                Marshal.PtrToStringUni(sources[1]) ?? throw new InvalidDataException("Missing ESA URL.")
            ];
        }
        finally
        {
            if (sources != null)
            {
                for (int i = 0; i < count; i++)
                    Marshal.FreeCoTaskMem(sources[i]);
                NativeMemory.Free(sources);
            }
        }
    }

    public sealed record Metadata(Version Version, Version StandardVersion, string Name, string Description, string Author,
        DateTime CreationDate, string? Icon, string[] UpdateSources);
}
