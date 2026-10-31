package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"time"
)

// Enabled only by an explicit diagnostic build flag, not by profile input.
var udpDiagnosticEnabled = "0"

func saveUDPDiagnostic(r lossSnapshot) {
	if udpDiagnosticEnabled != "1" {
		return
	}
	image, e := os.Executable()
	if e != nil {
		return
	}
	// This fixed basename inherits the protected component directory ACL.
	path := filepath.Join(filepath.Dir(image), "udp-diagnostic.json")
	data, e := json.Marshal(map[string]any{"pid": os.Getpid(), "updatedUtc": time.Now().UTC(), "outerSendAttempts": r.OuterSendAttempts, "outerSendAccepted": r.OuterSendAccepted, "outerSendErrorCalls": r.OuterSendErrors, "outerReceivedDatagrams": r.OuterReceiveDatagrams, "outerReceiveErrorCalls": r.OuterReceiveErrors, "relayInput": r.UdpRelayInput, "relayRejected": r.UdpRelayRejected, "tunnelWrites": r.UdpTunnelWrites, "tunnelWriteErrors": r.UdpTunnelWriteErrors, "tunnelReads": r.UdpTunnelReads, "relayReplies": r.UdpRelayReplies, "relayReplyErrors": r.UdpRelayReplyErrors, "budgetDrops": r.UdpBudgetDrops, "reasons": r.UdpReasons})
	if e != nil {
		return
	}
	temporary := path + ".tmp"
	if os.WriteFile(temporary, data, 0600) != nil {
		return
	}
	if os.Rename(temporary, path) != nil {
		os.Remove(temporary)
	}
}
