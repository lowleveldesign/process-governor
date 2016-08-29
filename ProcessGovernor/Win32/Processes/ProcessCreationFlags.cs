// Copyright 2013 The Chromium Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

using System;

namespace VsChromium.Core.Win32.Processes {
  [Flags]
  public enum ProcessCreationFlags : int {
    /// <summary>
    /// The calling thread starts and debugs the new process and all child
    /// processes created by the new process. It can receive all related debug
    /// events using the WaitForDebugEvent function. A process that uses
    /// DEBUG_PROCESS becomes the root of a debugging chain. This continues
    /// until another process in the chain is created with DEBUG_PROCESS. If
    /// this flag is combined with DEBUG_ONLY_THIS_PROCESS, the caller debugs
    /// only the new process, not any child processes.
    /// </summary>
    DEBUG_PROCESS = 0x00000001,
    /// <summary>
    /// The calling thread starts and debugs the new process. It can receive all
    /// related debug events using the WaitForDebugEvent function.
    /// </summary>
    DEBUG_ONLY_THIS_PROCESS = 0x00000002,
    /// <summary>
    /// The child processes of a process associated with a job are not
    /// associated with the job. If the calling process is not associated with a
    /// job, this constant has no effect. If the calling process is associated
    /// with a job, the job must set the JOB_OBJECT_LIMIT_BREAKAWAY_OK limit.
    /// </summary>
    CREATE_BREAKAWAY_FROM_JOB = 0x01000000,
    /// <summary>
    /// The process is a console application that is being run without a console
    /// window. Therefore, the console handle for the application is not set.
    /// This flag is ignored if the application is not a console application, or
    /// if it is used with either CREATE_NEW_CONSOLE or DETACHED_PROCESS.
    /// </summary>
    CREATE_NO_WINDOW = 0x08000000,
    /// <summary>
    /// The primary thread of the new process is created in a suspended state,
    /// and does not run until the ResumeThread function is called.
    CREATE_SUSPENDED = 0x00000004,
    /// <summary>
    /// The new process has a new console, instead of inheriting its parent's console (the default). 
    /// 
    /// This flag cannot be used with DETACHED_PROCESS.
    /// </summary>
    CREATE_NEW_CONSOLE = 0x00000010
  }
}
