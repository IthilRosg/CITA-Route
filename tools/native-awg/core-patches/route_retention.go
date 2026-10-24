package route

import (
	"fmt"
	"github.com/sagernet/sing-box/common/citaretain"
	"github.com/sagernet/sing/common"
	"io"
	"os"
)

// Retained entries STAY tracked so explicit reset/CloseAll still owns them.
func (m *ConnectionManager) CloseForInterfaceChange() {
	m.access.Lock()
	var closers []io.Closer
	retained := 0
	for element := m.connections.Front(); element != nil; {
		next := element.Next()
		if !citaretain.Preserve(element.Value) {
			closers = append(closers, element.Value)
			m.connections.Remove(element)
		} else {
			retained++
		}
		element = next
	}
	m.access.Unlock()
	for _, closer := range closers {
		common.Close(closer)
	}
	// Scalar lab receipt, no addresses/config/credentials. Needed to distinguish
	// actual reset handling from OS socket behavior or late interface callbacks.
	fmt.Fprintf(os.Stderr, "CITA_LAB_INTERFACE_RESET closed=%d retained=%d\n", len(closers), retained)
}
