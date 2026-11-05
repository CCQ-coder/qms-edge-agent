package main

import (
	"io"
	"log"
	"net/http"
)

type countedBody struct {
	io.ReadCloser
	read int
}

func (b *countedBody) Read(p []byte) (int, error) {
	n, err := b.ReadCloser.Read(p)
	b.read += n
	return n, err
}

type statusWriter struct {
	http.ResponseWriter
	status int
}

func (w *statusWriter) WriteHeader(status int) {
	if w.status == 0 {
		w.status = status
	}
	w.ResponseWriter.WriteHeader(status)
}

func (w *statusWriter) Write(p []byte) (int, error) {
	if w.status == 0 {
		w.status = http.StatusOK
	}
	return w.ResponseWriter.Write(p)
}

func logRequests(entry string, next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		response := &statusWriter{ResponseWriter: w}
		body := &countedBody{ReadCloser: r.Body}
		if r.Body != nil {
			r.Body = body
		}
		defer func() {
			status := response.status
			if status == 0 {
				status = http.StatusOK
			}
			log.Printf("JDL request entry=%q transport=\"http\" remote=%q method=%q path=%q read_bytes=%d status=%d", entry, r.RemoteAddr, r.Method, r.URL.Path, body.read, status)
		}()
		next.ServeHTTP(response, r)
	})
}
