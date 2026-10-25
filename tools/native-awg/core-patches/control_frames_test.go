package socks

import (
	"io"
	"strings"
	"testing"
)

func TestCitaRetentionPersistentFramesBoundEachMessageNotSession(t *testing.T) {
	input := newBoundedFrames(strings.NewReader(strings.Repeat("{\"Value\":1}\n", 4096)), 64)
	for i := 0; i < 4096; i++ {
		var value struct{ Value int }
		if input.Decode(&value) != nil || value.Value != 1 {
			t.Fatal("bounded valid session was truncated")
		}
	}
	var value struct{ Value int }
	if input.Decode(&value) != io.EOF {
		t.Fatal("EOF ownership lease missing")
	}
	for _, text := range []string{strings.Repeat(" ", 65) + "\n", "{\"Other\":1}\n", "{\"Value\":1} {\"Value\":2}\n", strings.Repeat("\n", 9)} {
		var v struct{ Value int }
		if newBoundedFrames(strings.NewReader(text), 64).Decode(&v) == nil {
			t.Fatal("unbounded or ambiguous control frame accepted")
		}
	}
}
