import { describe, expect, it } from 'vitest'
import { captureMatchesFilters } from '../hooks/useCaptures'
import type { TrafficCaptureEvent } from '../types/api'

const connect: TrafficCaptureEvent = {
  id: '1', deviceId: 'dev-sensor', method: 'CONNECT', scheme: 'tls-passthrough',
  host: 'telemetry.example.net', port: 443, path: '/', query: '',
  statusCode: 0, statusMessage: 'passthrough: auto', protocol: 'TlsPassthrough', isTls: true,
  tlsVersion: 'TLS 1.2', timestamp: '2026-11-03T20:00:00Z', durationMs: 0,
  clientIp: '192.0.2.204', requestBodySize: 0, responseBodySize: 0,
}

describe('captureMatchesFilters', () => {
  it('passes everything with no filters', () => {
    expect(captureMatchesFilters(connect, { page: 1, pageSize: 50 })).toBe(true)
  })

  it('rejects a CONNECT when filtering by GET', () => {
    expect(captureMatchesFilters(connect, { method: 'GET' })).toBe(false)
    expect(captureMatchesFilters({ ...connect, method: 'GET' }, { method: 'get' })).toBe(true)
  })

  it('matches host and client IP as substrings', () => {
    expect(captureMatchesFilters(connect, { host: 'telemetry' })).toBe(true)
    expect(captureMatchesFilters(connect, { host: 'bing.com' })).toBe(false)
    expect(captureMatchesFilters(connect, { clientIp: '2.204' })).toBe(true)
    expect(captureMatchesFilters(connect, { clientIp: '2.111' })).toBe(false)
  })

  it('matches device and status exactly', () => {
    expect(captureMatchesFilters(connect, { deviceId: 'dev-sensor' })).toBe(true)
    expect(captureMatchesFilters(connect, { deviceId: 'dev-camera' })).toBe(false)
    expect(captureMatchesFilters(connect, { statusCode: 200 })).toBe(false)
  })

  it('respects the time range', () => {
    expect(captureMatchesFilters(connect, { from: '2026-11-03T19:00:00Z' })).toBe(true)
    expect(captureMatchesFilters(connect, { to: '2026-11-03T19:00:00Z' })).toBe(false)
  })

  it('holds back live events while a body search is active', () => {
    expect(captureMatchesFilters(connect, { q: 'telemetry' })).toBe(false)
  })
})
