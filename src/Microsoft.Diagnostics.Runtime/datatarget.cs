﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Diagnostics.Runtime.Desktop;
using Microsoft.Diagnostics.Runtime.Interop;
using Microsoft.Diagnostics.Runtime.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Diagnostics.Runtime.ICorDebug;
using System.Text.RegularExpressions;
using System.ComponentModel;
using Microsoft.Diagnostics.Runtime.DacInterface;

namespace Microsoft.Diagnostics.Runtime
{
    /// <summary>
    /// The type of crash dump reader to use.
    /// </summary>
    public enum CrashDumpReader
    {
        /// <summary>
        /// Use DbgEng.  This allows the user to obtain an instance of IDebugClient through the
        /// DataTarget.DebuggerInterface property, at the cost of strict threading requirements.
        /// </summary>
        DbgEng,

        /// <summary>
        /// Use a simple dump reader to read data out of the crash dump.  This allows processing
        /// multiple dumps (using separate DataTargets) on multiple threads, but the
        /// DataTarget.DebuggerInterface property will return null.
        /// </summary>
        ClrMD
    }


    /// <summary>
    /// A crash dump or live process to read out of.
    /// </summary>
    public abstract class DataTarget : IDisposable
    {
        internal static PlatformFunctions PlatformFunctions { get; }

        static DataTarget()
        {
#if !NET45
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                PlatformFunctions = new LinuxFunctions();
            else
#endif
            PlatformFunctions = new WindowsFunctions();
        }


        /// <summary>
        /// Creates a DataTarget from a crash dump.
        /// </summary>
        /// <param name="fileName">The crash dump's filename.</param>
        /// <returns>A DataTarget instance.</returns>
        public static DataTarget LoadCrashDump(string fileName)
        {
            DbgEngDataReader reader = new DbgEngDataReader(fileName);
            return CreateFromReader(reader, reader.DebuggerInterface);
        }

        /// <summary>
        /// Creates a DataTarget from a coredump.  Note that since we have to load a native library (libmscordaccore.so)
        /// this must be run on a Linux machine.
        /// </summary>
        /// <param name="filename">The path to a core dump.</param>
        /// <returns>A DataTarget instance.</returns>
        public static DataTarget LoadCoreDump(string filename)
        {
            CoreDumpReader reader = new CoreDumpReader(filename);
            return CreateFromReader(reader, null);
        }


        /// <summary>
        /// Creates a DataTarget from a crash dump, specifying the dump reader to use.
        /// </summary>
        /// <param name="fileName">The crash dump's filename.</param>
        /// <param name="dumpReader">The type of dump reader to use.</param>
        /// <returns>A DataTarget instance.</returns>
        public static DataTarget LoadCrashDump(string fileName, CrashDumpReader dumpReader)
        {
            if (dumpReader == CrashDumpReader.DbgEng)
            {
                DbgEngDataReader reader = new DbgEngDataReader(fileName);
                return CreateFromReader(reader, reader.DebuggerInterface);
            }
            else
            {
                DumpDataReader reader = new DumpDataReader(fileName);
                return CreateFromReader(reader, null);
            }
        }

        /// <summary>
        /// Create an instance of DataTarget from a user defined DataReader
        /// </summary>
        /// <param name="reader">A user defined DataReader.</param>
        /// <returns>A new DataTarget instance.</returns>
        public static DataTarget CreateFromDataReader(IDataReader reader)
        {
            return CreateFromReader(reader, null);
        }

        private static DataTarget CreateFromReader(IDataReader reader, Interop.IDebugClient client)
        {
#if _TRACING
            reader = new TraceDataReader(reader);
#endif
            return new DataTargetImpl(reader, client);
        }

        /// <summary>
        /// Creates a data target from an existing IDebugClient interface.  If you created and attached
        /// a dbgeng based debugger to a process you may pass the IDebugClient RCW object to this function
        /// to create the DataTarget.
        /// </summary>
        /// <param name="client">The dbgeng IDebugClient object.  We will query interface on this for IDebugClient.</param>
        /// <returns>A DataTarget instance.</returns>
        public static DataTarget CreateFromDebuggerInterface(IDebugClient client)
        {
            DbgEngDataReader reader = new DbgEngDataReader(client);
            DataTargetImpl dataTarget = new DataTargetImpl(reader, reader.DebuggerInterface);

            return dataTarget;
        }

        /// <summary>
        /// Invasively attaches to a live process.
        /// </summary>
        /// <param name="pid">The process ID of the process to attach to.</param>
        /// <param name="msecTimeout">Timeout in milliseconds.</param>
        /// <returns>A DataTarget instance.</returns>
        public static DataTarget AttachToProcess(int pid, uint msecTimeout)
        {
            return AttachToProcess(pid, msecTimeout, AttachFlag.Invasive);
        }

        /// <summary>
        /// Attaches to a live process.
        /// </summary>
        /// <param name="pid">The process ID of the process to attach to.</param>
        /// <param name="msecTimeout">Timeout in milliseconds.</param>
        /// <param name="attachFlag">The type of attach requested for the target process.</param>
        /// <returns>A DataTarget instance.</returns>
        public static DataTarget AttachToProcess(int pid, uint msecTimeout, AttachFlag attachFlag)
        {
            IDebugClient client = null;
            IDataReader reader;
            if (attachFlag == AttachFlag.Passive)
            {
                reader = new LiveDataReader(pid, createSnapshot: false);
            }
            else
            {
                var dbgeng = new DbgEngDataReader(pid, attachFlag, msecTimeout);
                reader = dbgeng;
                client = dbgeng.DebuggerInterface;
            }

            DataTargetImpl dataTarget = new DataTargetImpl(reader, client);
            return dataTarget;
        }

        /// <summary>
        /// Attaches to a snapshot process (see https://msdn.microsoft.com/en-us/library/dn457825(v=vs.85).aspx).
        /// </summary>
        /// <param name="pid">The process ID of the process to attach to.</param>
        /// <returns>A DataTarget instance.</returns>
        public static DataTarget CreateSnapshotAndAttach(int pid)
        {
            IDataReader reader = new LiveDataReader(pid, createSnapshot: true);
            DataTargetImpl dataTarget = new DataTargetImpl(reader, null);
            return dataTarget;
        }

        /// <summary>
        /// The data reader for this instance.
        /// </summary>
        public abstract IDataReader DataReader { get; }

        private SymbolLocator _symbolLocator;
        /// <summary>
        /// Instance to manage the symbol path(s)
        /// </summary>
        public SymbolLocator SymbolLocator
        {
            get
            {
                if (_symbolLocator == null)
                    _symbolLocator = new DefaultSymbolLocator();

                return _symbolLocator;
            }
            set
            {
                _symbolLocator = value;
            }
        }

        /// <summary>
        /// A symbol provider which loads PDBs on behalf of ClrMD.  This should be set so that when ClrMD needs to
        /// resolve names which can only come from PDBs.  If this is not set, you may have a degraded experience.
        /// </summary>
        public ISymbolProvider SymbolProvider { get; set; }

        FileLoader _fileLoader;
        internal FileLoader FileLoader
        {
            get
            {
                if (_fileLoader == null)
                    _fileLoader = new FileLoader(this);

                return _fileLoader;
            }
        }

        /// <summary>
        /// Returns true if the target process is a minidump, or otherwise might have limited memory.  If IsMinidump
        /// returns true, a greater range of functions may fail to return data due to the data not being present in
        /// the application/crash dump you are debugging.
        /// </summary>
        public abstract bool IsMinidump { get; }

        /// <summary>
        /// Returns the architecture of the target process or crash dump.
        /// </summary>
        public abstract Architecture Architecture { get; }

        /// <summary>
        /// Returns the list of Clr versions loaded into the process.
        /// </summary>
        public abstract IList<ClrInfo> ClrVersions { get; }

        /// <summary>
        /// Returns the pointer size for the target process.
        /// </summary>
        public abstract uint PointerSize { get; }

        /// <summary>
        /// Reads memory from the target.
        /// </summary>
        /// <param name="address">The address to read from.</param>
        /// <param name="buffer">The buffer to store the data in.  Size must be greator or equal to
        /// bytesRequested.</param>
        /// <param name="bytesRequested">The amount of bytes to read from the target process.</param>
        /// <param name="bytesRead">The actual number of bytes read.</param>
        /// <returns>True if any bytes were read out of the process (including a partial read).  False
        /// if no bytes could be read from the address.</returns>
        public abstract bool ReadProcessMemory(ulong address, byte[] buffer, int bytesRequested, out int bytesRead);

        /// <summary>
        /// Returns the IDebugClient interface associated with this datatarget.  (Will return null if the
        /// user attached passively.)
        /// </summary>
        public abstract IDebugClient DebuggerInterface { get; }

        /// <summary>
        /// Enumerates information about the loaded modules in the process (both managed and unmanaged).
        /// </summary>
        public abstract IEnumerable<ModuleInfo> EnumerateModules();

        /// <summary>
        /// IDisposable implementation.
        /// </summary>
        public abstract void Dispose();

        internal abstract void AddDacLibrary(DacLibrary dacLibrary);
    }
}
