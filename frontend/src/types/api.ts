// ── Enums ────────────────────────────────────────────────────────────────────

export type InterceptionProtocol =
  | 'Http'
  | 'Https'
  | 'Mqtt'
  | 'MqttTls'
  | 'CoAP'
  | 'Dns'
  | 'MDns'
  | 'WebSocket'
  | 'WebSocketTls'
  | 'Grpc'
  | 'Modbus'
  | 'TlsPassthrough'
  | 'Other'

export type ProxyMode = 'ExplicitProxy' | 'ArpSpoof' | 'GatewayRedirect' | 'Passive'

// ── Domain models ─────────────────────────────────────────────────────────────

export interface CapturedRequest {
  id: string
  deviceId?: string
  device?: Device
  // Request
  method: string
  scheme: string
  host: string
  port: number
  path: string
  query: string
  requestHeaders: string   // JSON-serialized dictionary
  requestBody: string
  requestBodySize: number
  // Response
  statusCode: number
  statusMessage: string
  responseHeaders: string  // JSON-serialized dictionary
  responseBody: string
  responseBodySize: number
  // TLS
  isTls: boolean
  tlsVersion: string
  tlsCipherSuite: string
  // Meta
  protocol: InterceptionProtocol
  timestamp: string        // ISO 8601
  durationMs: number
  clientIp: string
  isModified: boolean
  notes: string
}

export interface ExportCaptureAsAssetResult {
  fileName: string
  filePath: string
  contentType: string
  sizeBytes: number
}

export interface Device {
  id: string
  ipAddress: string
  macAddress: string
  hostname: string
  vendor: string
  label: string
  notes: string
  /** Comma-separated tags (e.g. "camera,upstairs,vendor-x"). */
  tags?: string | null
  interceptionEnabled: boolean
  firstSeen: string
  lastSeen: string
  securityScore: number    // -1 = unscored
}

export interface ProxySettings {
  id: number
  proxyPort: number
  mode: ProxyMode
  isRunning: boolean
  captureTls: boolean
  captureRequestBodies: boolean
  captureResponseBodies: boolean
  maxBodySizeKb: number
  listenAddress: string
  passwordHash: string
  autoStart: boolean
  // GatewayRedirect / ArpSpoof settings
  transparentProxyPort: number
  targetDeviceIp: string
  gatewayIp: string
  networkInterface: string
  // CA customization
  caCommonName: string
  caOrganization: string
  caCountry: string
  caValidityYears: number
}

export interface CertificateEntry {
  id: string
  commonName: string
  subjectAltNames: string
  certificatePem: string
  privateKeyPem: string
  notBefore: string
  notAfter: string
  serialNumber: string
  isRootCa: boolean
  createdAt: string
}

/** Summary shape returned by the list endpoint — excludes requestBody and responseBody */
export interface CapturedRequestSummary {
  id: string
  deviceId?: string
  device?: Device
  // Request
  method: string
  scheme: string
  host: string
  port: number
  path: string
  query: string
  requestHeaders: string
  requestBodySize: number
  // Response
  statusCode: number
  statusMessage: string
  responseHeaders: string
  responseBodySize: number
  // TLS
  isTls: boolean
  tlsVersion: string
  tlsCipherSuite: string
  // Meta
  protocol: InterceptionProtocol
  timestamp: string
  durationMs: number
  clientIp: string
  isModified: boolean
  notes: string
}

// ── API response shapes ───────────────────────────────────────────────────────

export interface CaptureListResponse {
  items: CapturedRequestSummary[]
  total: number
  page: number
  pageSize: number
  pages: number
}

export interface ProxyStatus {
  isRunning: boolean
  port: number
  settings: ProxySettings
}

export interface AuthStatusResponse {
  passwordSet: boolean
  multiUser?: boolean
}

export type UserRole = 'admin' | 'operator' | 'viewer'

export interface UserInfo {
  id: string
  username: string
  displayName: string
  role: UserRole
  isEnabled: boolean
  createdAt: string
  lastLoginAt?: string
}

export interface AuditEntry {
  id: string
  userId?: string
  username: string
  action: string
  entityType: string
  entityId?: string
  details?: string
  ipAddress: string
  timestamp: string
}

export interface DashboardLayout {
  id: string
  userId: string
  name: string
  isDefault: boolean
  layoutJson: string
  filtersJson: string
  createdAt: string
  updatedAt: string
}

export interface LoginResponse {
  token: string
  user?: CurrentUser
}

export interface CurrentUser {
  id: string
  username: string
  displayName: string
  role: string
}

export interface AdminDataStats {
  count: number
  estimatedSizeBytes: number
  oldestTimestamp: string | null
}

export interface AdminStats {
  captures: AdminDataStats
  packets: AdminDataStats
  scanFindings: { count: number }
  auditLog: {
    count: number
    archiveCount: number
    oldestTimestamp: string | null
    oldestArchiveTimestamp: string | null
  }
}

export interface UserSummary {
  id: string
  username: string
  displayName: string
  role: string
  isEnabled: boolean
  createdAt: string
  lastLoginAt: string | null
}

export interface ApiKeySummary {
  id: string
  name: string
  scopes: string[]
  expiresAt: string | null
  lastUsedAt: string | null
  ownerId: string
  isRevoked: boolean
  createdAt: string
}

export interface ApiKeyCreated extends ApiKeySummary {
  /** Plaintext key — returned once at creation/rotation only. */
  key: string
}

export interface RootCaSummary {
  id: string
  commonName: string
  serialNumber: string
  notBefore: string
  notAfter: string
  isRootCa: boolean
  createdAt: string
  certificatePem: string
}

// ── Request payloads ──────────────────────────────────────────────────────────

export interface LoginRequest {
  username: string
  password: string
}

export interface SetupRequest {
  password: string
}

export interface DevicePatchRequest {
  label?: string
  notes?: string
  tags?: string
  interceptionEnabled?: boolean
}

export interface ProxySettingsUpdate {
  proxyPort?: number
  listenAddress?: string
  captureTls?: boolean
  captureRequestBodies?: boolean
  captureResponseBodies?: boolean
  maxBodySizeKb?: number
  mode?: ProxyMode
  autoStart?: boolean
  transparentProxyPort?: number
  targetDeviceIp?: string
  gatewayIp?: string
  networkInterface?: string
  caCommonName?: string
  caOrganization?: string
  caCountry?: string
  caValidityYears?: number
}

// ── Filter params ─────────────────────────────────────────────────────────────

export interface CaptureFilters {
  deviceId?: string
  host?: string
  method?: string
  statusCode?: number
  from?: string
  to?: string
  q?: string
  clientIp?: string
  page?: number
  pageSize?: number
}

// ── Scanner enums ────────────────────────────────────────────────────────────

export type ScanStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'

export type ScanFindingSeverity = 'Info' | 'Low' | 'Medium' | 'High' | 'Critical'

export type ScanFindingType =
  | 'OpenPort'
  | 'ServiceBanner'
  | 'DefaultCredential'
  | 'Cve'
  | 'ConfigIssue'

// ── Scanner models ───────────────────────────────────────────────────────────

export interface ScanJob {
  id: string
  deviceId: string
  device?: Device
  status: ScanStatus
  portRange: string
  maxConcurrency: number
  timeoutMs: number
  enableFingerprinting: boolean
  enableCredentialTest: boolean
  enableCveLookup: boolean
  enableConfigAudit: boolean
  portsScanned: number
  openPortsFound: number
  findingsCount: number
  startedAt?: string
  completedAt?: string
  errorMessage?: string
  createdAt: string
  findings?: ScanFinding[]
}

export interface ScanFinding {
  id: string
  scanJobId: string
  type: ScanFindingType
  severity: ScanFindingSeverity
  title: string
  description: string

  // Port scan / service info
  port?: number
  protocol?: string
  serviceName?: string
  banner?: string
  cpe?: string

  // Credential test
  username?: string
  password?: string

  // CVE info
  cveId?: string
  cvssScore?: number
  cveDescription?: string
  reference?: string

  foundAt: string
}

// ── Scanner request DTOs ─────────────────────────────────────────────────────

export interface StartScanRequest {
  deviceId: string
  portRange?: string
  maxConcurrency?: number
  timeoutMs?: number
  enableFingerprinting?: boolean
  enableCredentialTest?: boolean
  enableCveLookup?: boolean
  enableConfigAudit?: boolean
  consentAcknowledged: boolean
}

// ── Scan scope models ─────────────────────────────────────────────────────────

export interface ScanScope {
  id: string
  name: string
  cidr: string
  isActive: boolean
  createdByUsername: string
  createdAt: string
}

// ── Manipulation enums ───────────────────────────────────────────────────────

export type ManipulationRuleAction =
  | 'ModifyHeader'
  | 'ModifyBody'
  | 'ModifyStatus'
  | 'Delay'
  | 'Drop'

export type ManipulationPhase = 'Request' | 'Response'

export type ScriptLanguage = 'CSharp' | 'JavaScript'

export type FuzzerStrategy = 'Random' | 'Boundary' | 'BitFlip'

export type FuzzerJobStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled'

// ── Manipulation models ──────────────────────────────────────────────────────

export interface ManipulationRule {
  id: string
  name: string
  enabled: boolean
  priority: number
  phase: ManipulationPhase
  action: ManipulationRuleAction
  hostPattern?: string
  pathPattern?: string
  methodPattern?: string
  headerName?: string
  headerValue?: string
  bodyReplace?: string
  bodyReplaceWith?: string
  overrideStatusCode?: number
  delayMs?: number
  createdAt: string
  updatedAt: string
}

export interface Breakpoint {
  id: string
  name: string
  enabled: boolean
  language: ScriptLanguage
  phase: ManipulationPhase
  hostPattern?: string
  pathPattern?: string
  scriptCode: string
  createdAt: string
  updatedAt: string
}

export interface ReplaySession {
  id: string
  captureId: string
  method: string
  host: string
  path: string
  requestHeaders: string
  requestBody: string
  responseStatusCode: number
  responseHeaders: string
  responseBody: string
  durationMs: number
  bypassTlsValidation: boolean
  createdAt: string
}

export interface FuzzerJob {
  id: string
  captureId: string
  strategy: FuzzerStrategy
  status: FuzzerJobStatus
  mutationCount: number
  concurrentRequests: number
  completedRequests: number
  anomaliesFound: number
  bypassTlsValidation: boolean
  startedAt?: string
  completedAt?: string
  errorMessage?: string
  createdAt: string
}

export interface FuzzerResult {
  id: string
  fuzzerJobId: string
  mutationIndex: number
  mutatedBody: string
  responseStatusCode: number
  responseBody: string
  durationMs: number
  isAnomaly: boolean
  anomalyReason?: string
  createdAt: string
}

// ── Manipulation request DTOs ────────────────────────────────────────────────

export interface CreateManipulationRuleRequest {
  name: string
  enabled?: boolean
  priority?: number
  phase?: ManipulationPhase
  action: ManipulationRuleAction
  hostPattern?: string
  pathPattern?: string
  methodPattern?: string
  headerName?: string
  headerValue?: string
  bodyReplace?: string
  bodyReplaceWith?: string
  overrideStatusCode?: number
  delayMs?: number
}

export interface UpdateManipulationRuleRequest extends CreateManipulationRuleRequest {
  id: string
}

export interface CreateBreakpointRequest {
  name: string
  enabled?: boolean
  language: ScriptLanguage
  phase?: ManipulationPhase
  hostPattern?: string
  pathPattern?: string
  scriptCode: string
}

export interface UpdateBreakpointRequest extends CreateBreakpointRequest {
  id: string
}

export interface CreateReplayRequest {
  captureId: string
  method?: string
  host?: string
  port?: number
  path?: string
  query?: string
  requestHeaders?: string
  requestBody?: string
  bypassTlsValidation?: boolean
}

export interface StartFuzzerRequest {
  captureId: string
  strategy?: FuzzerStrategy
  mutationCount?: number
  concurrentRequests?: number
  bypassTlsValidation?: boolean
}

// ── OpenRTB ──────────────────────────────────────────────────────────────────

export type OpenRtbMessageType = 'BidRequest' | 'BidResponse'

export type PiiRedactionStrategy =
  | 'Redact'
  | 'TruncateIp'
  | 'HashSha256'
  | 'GeneralizeGeo'
  | 'GeneralizeUserAgent'
  | 'Remove'

export interface OpenRtbEvent {
  id: string
  capturedRequestId: string
  version: string
  messageType: OpenRtbMessageType
  impressionCount: number
  bidCount: number
  hasDeviceInfo: boolean
  hasUserData: boolean
  hasGeoData: boolean
  exchange: string
  seatBids?: string
  rawJson: string
  detectedAt: string
}

export interface OpenRtbPiiPolicy {
  id: string
  enabled: boolean
  fieldPath: string
  strategy: PiiRedactionStrategy
  hostPattern?: string
  priority: number
  createdAt: string
  updatedAt: string
}

export interface CreatePiiPolicyRequest {
  fieldPath: string
  strategy: PiiRedactionStrategy
  enabled?: boolean
  hostPattern?: string
  priority?: number
}

export interface UpdatePiiPolicyRequest {
  fieldPath?: string
  strategy?: PiiRedactionStrategy
  enabled?: boolean
  hostPattern?: string
  priority?: number
}

export interface PiiStrippingLog {
  id: string
  capturedRequestId: string
  host: string
  path: string
  fieldPath: string
  strategy: PiiRedactionStrategy
  originalValueHash: string
  redactedPreview: string
  phase: string
  strippedAt: string
}

export interface PiiAuditStats {
  totalStripped: number
  byFieldPath: Record<string, number>
  byHost: Record<string, number>
}

export interface PagedResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

// ── API Spec enums ──────────────────────────────────────────────────────────

export type ApiSpecStatus = 'Draft' | 'Active' | 'Archived'

export type ContentMatchType = 'ContentType' | 'JsonPath' | 'HeaderValue' | 'BodyRegex'

export type ContentReplacementAction =
  | 'ReplaceWithFile'
  | 'ReplaceWithUrl'
  | 'ReplaceWithValue'
  | 'Redact'
  | 'TrackingPixel'
  | 'MockSseStream'

// ── API Spec models ─────────────────────────────────────────────────────────

export interface ApiSpecDocument {
  id: string
  name: string
  description: string
  host: string
  version: string
  openApiJson: string
  status: ApiSpecStatus
  mockEnabled: boolean
  passthroughFirst: boolean
  useLlmAnalysis: boolean
  createdAt: string
  updatedAt: string
}

export interface ContentReplacementRule {
  id: string
  apiSpecDocumentId: string
  host?: string
  name: string
  enabled: boolean
  matchType: ContentMatchType
  matchPattern: string
  action: ContentReplacementAction
  replacementValue?: string
  replacementFilePath?: string
  replacementContentType?: string
  hostPattern?: string
  pathPattern?: string
  priority: number
  createdAt: string
  sseInterEventDelayMs?: number
  sseLoop?: boolean
}

export interface AssetInfo {
  filePath: string
  fileName: string
  size: number
  lastModified: string
}

export interface SyntheticHttpMessage {
  method?: string
  host?: string
  path?: string
  requestHeaders?: Record<string, string>
  requestBody?: string
  statusCode?: number
  responseHeaders?: Record<string, string>
  responseBody?: string
}

export interface PreviewRuleRequest {
  captureId?: string
  synthetic?: SyntheticHttpMessage
}

export interface PreviewRuleResult {
  matched: boolean
  modified: boolean
  statusCode: number
  responseHeaders: Record<string, string>
  responseBodyText?: string
  responseBodyBase64?: string
  bodyLength: number
  contentType?: string
  warnings: string[]
  wasStreamed: boolean
}

// ── API Spec request DTOs ───────────────────────────────────────────────────

export interface GenerateSpecRequest {
  host: string
  pathPattern?: string
  method?: string
  from?: string
  to?: string
  useLlmAnalysis?: boolean
  name?: string
}

export interface ImportSpecRequest {
  openApiJson: string
  name?: string
}

export interface UpdateSpecRequest {
  name?: string
  description?: string
  host?: string
  version?: string
  mockEnabled?: boolean
  passthroughFirst?: boolean
  status?: ApiSpecStatus
}

// ── Plugin models ────────────────────────────────────────────────────────────

export type PluginTrustStatus =
  | 'Trusted'
  | 'Untrusted'
  | 'ManifestMissing'
  | 'ManifestInvalid'
  | 'HashMismatch'
  | 'SignatureInvalid'

export interface PluginInfo {
  protocol: string
  name: string
  version: string
  assemblyPath: string
  isLoaded: boolean
  loadError?: string
  trustStatus: PluginTrustStatus
  signerSubject?: string
}

// ── Protocol Proxy models ─────────────────────────────────────────────────────

export interface MqttBrokerSettings {
  enabled: boolean
  listenPort: number
  listenAddress: string
  upstreamHost?: string
  upstreamPort: number
  logPayloads: boolean
  topicFilters: string[]
}

export interface CoapProxySettings {
  enabled: boolean
  listenPort: number
  listenAddress: string
  upstreamHost?: string
  upstreamPort: number
  logPayloads: boolean
}

export interface MqttProxyStatus {
  isRunning: boolean
  activeConnections: number
}

export interface CoapProxyStatus {
  isRunning: boolean
  messagesProxied: number
}

// ── SignalR DTOs ──────────────────────────────────────────────────────────────

/** Trimmed capture event pushed via SignalR — no body content */
export interface TrafficCaptureEvent {
  id: string
  deviceId?: string
  method: string
  scheme: string
  host: string
  port: number
  path: string
  query: string
  statusCode: number
  statusMessage: string
  protocol: InterceptionProtocol
  isTls: boolean
  tlsVersion: string
  timestamp: string
  durationMs: number
  clientIp: string
  requestBodySize: number
  responseBodySize: number
}

// ── Packet Capture types ──────────────────────────────────────────────────────

export interface NetworkDevice {
  name: string
  displayName: string
  ipAddresses: string[]
  macAddress?: string
  isLoopback: boolean
  isUp: boolean
  isRunning: boolean
}

export interface CapturedPacket {
  id: string
  timestamp: string
  sourceIp: string
  destinationIp: string
  sourcePort: number
  destinationPort: number
  protocol: string
  length: number
  payloadPreview?: string
  tcpFlags?: string
  isError?: boolean
  isRetransmission?: boolean
  /** "Live" for real-time capture, "Import" for PCAP file imports */
  source?: string
}

export interface PcapImportResult {
  jobId: string
  packetsImported: number
  packetsSkipped: number
  tcpSessionsReconstructed: number
}

export interface CaptureDeviceDto {
  id: string
  name: string
  displayName: string
  ipAddress: string
  macAddress: string
}

export interface FreezeFrameDto {
  packetId: string
  timestamp: string
  fullPayloadHex: string
  hexDump: string
  protocolDetails: string
  layer2Info: string
  layer3Info: string
  layer4Info: string
}

export interface ProtocolStatsDto {
  name: string
  count: number
  percentage: number
}

export interface ProtocolDistributionDto {
  totalPackets: number
  byProtocol: ProtocolStatsDto[]
  byLayer3: ProtocolStatsDto[]
  byLayer4: ProtocolStatsDto[]
}

export interface CommunicationPatternDto {
  sourceIp: string
  destinationIp: string
  packetCount: number
  totalBytes: number
  protocolsUsed: string[]
  firstSeen?: string
  lastSeen?: string
}

export interface SuspiciousActivityDto {
  id: string
  category: string
  severity: string
  description: string
  sourceIp: string
  destinationIp?: string
  packetCount: number
  firstDetected: string
  evidence: string[]
}

// ── Phase 21 — Passive proxy ──────────────────────────────────────────────────

export interface EndpointFrequency {
  method: string
  host: string
  path: string
  count: number
}

export interface StatusCodeBucket {
  statusCode: number
  count: number
}

export interface PassiveCaptureSummary {
  totalRequests: number
  topEndpoints: EndpointFrequency[]
  statusCodes: StatusCodeBucket[]
  topHosts: string[]
  activeDeviceFilter: string[]
}

export interface PassiveCaptureSession {
  id: string
  name: string
  description?: string
  createdAt: string
  entryCount: number
  deviceFilter?: string
}

