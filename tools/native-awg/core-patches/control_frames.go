package socks

import (
	"bufio"
	"bytes"
	"encoding/json"
	"errors"
	"io"
)

type messageDecoder interface{ Decode(any) error }
type boundedFrames struct {
	reader *bufio.Reader
	limit  int
}

func newBoundedFrames(input io.Reader, limit int) *boundedFrames {
	return &boundedFrames{bufio.NewReaderSize(input, 4096), limit}
}
func (f *boundedFrames) Decode(value any) error {
	for blanks := 0; blanks < 8; blanks++ {
		var frame []byte
		for {
			part, e := f.reader.ReadSlice('\n')
			if len(frame)+len(part) > f.limit {
				return errors.New("control frame limit")
			}
			frame = append(frame, part...)
			if e == bufio.ErrBufferFull {
				continue
			}
			if e != nil && e != io.EOF {
				return e
			}
			if len(frame) == 0 && e == io.EOF {
				return io.EOF
			}
			break
		}
		if len(bytes.TrimSpace(frame)) == 0 {
			continue
		}
		decoder := json.NewDecoder(bytes.NewReader(frame))
		decoder.DisallowUnknownFields()
		if e := decoder.Decode(value); e != nil {
			return e
		}
		var trailing any
		if decoder.Decode(&trailing) != io.EOF {
			return errors.New("multiple control objects")
		}
		return nil
	}
	return errors.New("empty control frames")
}
