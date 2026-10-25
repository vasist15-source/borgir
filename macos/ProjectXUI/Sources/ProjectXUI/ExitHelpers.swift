import Darwin

/// macOS keeps a quit app in the Dock ("exited with subordinates") while any helper process it started is still
/// running. Two of Borgir's outlive it by minutes: the Metal compiler service, and a Biome service that refuses the
/// ad hoc signed app a moment before it exits and then holds on to the half-open connection. This ends them as the
/// process exits, after AppKit and the core have shut down, so nothing is still being compiled. SIGTERM isn't enough:
/// an XPC service ignores it while a connection is open.
enum ExitHelpers {
    static func endOnExit() {
        atexit {
            ExitHelpers.endAll()
        }
    }

    private static func endAll() {
        // The same lookup Activity Monitor uses to file helpers under their app. Missing on some future macOS: then
        // the helpers end on their own, later.
        typealias ResponsiblePid = @convention(c) (pid_t) -> pid_t

        guard let symbol = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "responsibility_get_pid_responsible_for_pid") else {
            return
        }

        let responsiblePid = unsafeBitCast(symbol, to: ResponsiblePid.self)
        let me = getpid()
        var pids = [pid_t](repeating: 0, count: Int(proc_listallpids(nil, 0)) + 64)
        let count = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.stride))

        for pid in pids.prefix(Int(max(count, 0))) where pid > 0 && pid != me && responsiblePid(pid) == me {
            kill(pid, SIGKILL)
        }
    }
}
