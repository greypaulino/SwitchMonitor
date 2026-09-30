; Wait for loss of control, then one matching reading. The caller verifies a
; second reading after checking the monitor identity before sending anything.
class ReturnSyncState {
    __New(monitor, input) {
        this.monitor := monitor
        this.input := input
        this.disconnected := false
        this.matches := 0
    }

    Observe(current) {
        if !current {
            this.disconnected := true
            this.matches := 0
            return 'wait'
        }
        if !this.disconnected
            return 'wait'
        if current != this.input
            return 'cancel'
        this.matches += 1
        return this.matches >= 1 ? 'sync' : 'wait'
    }
}
