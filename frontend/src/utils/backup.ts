/**
 * 备份 / 恢复的纯函数层（不碰 DOM、不发请求，便于单测）。
 *
 * 契约以「旧 SolidJS 前端的备份文件」为真源（`frontend-legacy/src/pages/manage/backup-restore.tsx`）：
 * 一个 JSON，五个数组 + 一个 `encrypted` 标记；给了口令就**逐字段**加密。
 * 这里刻意把加密格式做成**逐字节一致**，这样：
 * - 旧前端导出的备份能在新前端恢复（迁移期的真实场景）；
 * - 新前端导出的备份也能拿回旧前端/Go 版恢复。
 *
 * 加密细节（CryptoJS 的 OpenSSL 口令格式，别自己改）：
 * 1. `AES.encrypt(JSON.stringify(value), password)` → `"U2FsdGVkX1..."`（base64 的 `Salted__`+盐+密文）；
 * 2. 再把这个 base64 **字符串**当 UTF-8 文本做一次 base64 —— 旧前端就是双 base64。
 * 解密反向：先 base64 还原成字符串，再 `AES.decrypt(...)`，最后 `JSON.parse`。
 * 口令错时 CryptoJS 返回空串（或解出乱码），JSON.parse 会抛 —— 统一归为「口令错误」。
 *
 * 为什么不自己实现 AES：上层只需要「和老前端互通」，用同一个库是唯一能保证一致的做法；
 * 自己手写 AES+MD5+EVP_BytesToKey 约 300 行，风险高且没有任何收益。
 * 注意本库已停止维护，因此固定 4.1.1（输出与旧前端的 4.2.0 完全一致），
 * 且**只用于读写这个历史格式**，不用于任何新协议。
 */
import CryptoJS from "crypto-js"
import type { Meta, SettingItem, StorageWithDriver } from "~/api/types"

/** 我们自己写的格式标记（旧文件没有这两个字段，导入时按缺省处理）。 */
export const BACKUP_FORMAT = "openlist-net-backup"
export const BACKUP_VERSION = 1

export interface BackupPayload {
  settings: SettingItem[]
  storages: StorageWithDriver[]
  metas: Meta[]
}

export interface BackupFile extends BackupPayload {
  /** 非空 = 逐字段加密；内容是 `AES("encrypted", 口令)` 的双 base64。 */
  encrypted: string
  format?: string
  version?: number
  created_at?: string
  site?: string
}

export type BackupSection = keyof BackupPayload

/** 导出顺序 = 恢复顺序：先设置（站点身份），再挂载，最后元数据。 */
export const BACKUP_SECTIONS: BackupSection[] = ["settings", "storages", "metas"]

export function emptyPayload(): BackupPayload {
  return { settings: [], storages: [], metas: [] }
}

// ---------------------------------------------------------------------------
// 加密 / 解密（CryptoJS 兼容格式）
// ---------------------------------------------------------------------------

/** 口令错、或密文被截断/篡改。 */
export class BackupCryptoError extends Error {
  constructor(message = "wrong password or corrupted backup") {
    super(message)
    this.name = "BackupCryptoError"
  }
}

function doubleBase64FromPlain(plain: string, key: string): string {
  // 第一次：AES 口令加密（自带随机盐，所以同一字段两次备份的密文不同）
  const cipher = CryptoJS.AES.encrypt(plain, key).toString()
  // 第二次：把 base64 字符串再 base64 一遍（旧前端的 `Base64.stringify(Utf8.parse(...))`）
  return CryptoJS.enc.Base64.stringify(CryptoJS.enc.Utf8.parse(cipher))
}

/** 解出明文；口令错时抛 <see cref="BackupCryptoError"/>。 */
function plainFromDoubleBase64(encoded: string, key: string): string {
  let inner: string
  try {
    inner = CryptoJS.enc.Base64.parse(encoded).toString(CryptoJS.enc.Utf8)
  } catch {
    throw new BackupCryptoError()
  }
  const plain = CryptoJS.AES.decrypt(inner, key).toString(CryptoJS.enc.Utf8)
  if (!plain) throw new BackupCryptoError()
  return plain
}

/** 单字段加密：值先 JSON 序列化，这样字符串/数字/布尔/数组都能原样还原。 */
export function encryptValue(value: unknown, key: string): string {
  return doubleBase64FromPlain(JSON.stringify(value), key)
}

export function decryptValue(encoded: string, key: string): unknown {
  const plain = plainFromDoubleBase64(encoded, key)
  try {
    return JSON.parse(plain)
  } catch {
    // 口令错时 AES 常常解出「非空但不是 JSON」的乱码，走这里
    throw new BackupCryptoError()
  }
}

/** 校验口令：标记字段解密后必须正好是 JSON 字符串 `"encrypted"`。 */
export function verifyPassword(file: Pick<BackupFile, "encrypted">, key: string): boolean {
  if (!file.encrypted) return true
  try {
    return plainFromDoubleBase64(file.encrypted, key) === JSON.stringify("encrypted")
  } catch {
    return false
  }
}

// ---------------------------------------------------------------------------
// 导出
// ---------------------------------------------------------------------------

/** 逐字段加密（不改入参）。 */
function sealPayload(payload: BackupPayload, key: string): BackupPayload {
  const sealed = emptyPayload()
  for (const section of BACKUP_SECTIONS) {
    // 三个域的实体类型互不兼容，这里只关心「一行是一个字段字典」
    const rows = payload[section] as unknown as Record<string, unknown>[]
    sealed[section] = rows.map((row) => {
      const out: Record<string, unknown> = {}
      for (const [field, value] of Object.entries(row)) out[field] = encryptValue(value, key)
      return out
    }) as never
  }
  return sealed
}

export interface SealOptions {
  /** 空串 = 明文导出。 */
  password?: string
  site?: string
  /** 注入时钟，便于单测。 */
  now?: Date
}

/** 生成备份对象（加密版或不加密版都走这里）。 */
export function sealBackup(payload: BackupPayload, options: SealOptions = {}): BackupFile {
  const password = options.password ?? ""
  const now = options.now ?? new Date()
  const sealed = password ? sealPayload(payload, password) : payload
  return {
    format: BACKUP_FORMAT,
    version: BACKUP_VERSION,
    created_at: now.toISOString(),
    site: options.site ?? "",
    encrypted: password ? doubleBase64FromPlain(JSON.stringify("encrypted"), password) : "",
    ...sealed,
  }
}

/** 落盘文本：两空格缩进，方便人眼核对（旧前端同样如此）。 */
export function serializeBackup(file: BackupFile): string {
  return JSON.stringify(file, null, 2)
}

/**
 * 备份文件名。
 *
 * 旧前端用 `new Date().toLocaleString()` 拼名字，在 Windows 上会得到
 * `openlist_backup_2026/10/27 11:30:00.json` —— 带 `/` 和空格的文件名根本存不下来。
 * 这里改成 `openlist_backup_<站点>_<YYYYMMDD-HHmmss>.json`。
 */
export function backupFileName(site: string, now: Date = new Date()): string {
  const pad = (n: number) => String(n).padStart(2, "0")
  const stamp =
    `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}` +
    `-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`
  const safeSite = (site || "openlist")
    .trim()
    // 去掉文件系统不接受的字符，并把空白压成下划线
    .replace(/[\\/:*?"<>|\s]+/g, "_")
    .replace(/^_+|_+$/g, "")
    .slice(0, 32)
  return `openlist_backup_${safeSite || "openlist"}_${stamp}.json`
}

// ---------------------------------------------------------------------------
// 导入：解析与校验
// ---------------------------------------------------------------------------

export type ParseFailure = "invalid_json" | "not_a_backup" | "no_sections"

export type ParseResult =
  | { ok: true; file: BackupFile }
  | { ok: false; reason: ParseFailure }

export function parseBackupText(text: string): ParseResult {
  let raw: unknown
  try {
    raw = JSON.parse(text)
  } catch {
    return { ok: false, reason: "invalid_json" }
  }
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    return { ok: false, reason: "not_a_backup" }
  }
  const file = raw as Partial<BackupFile>
  // 至少要有三个数组里的一个，才认为这是一份备份（旧文件也没有 format 字段）
  const hasSection = BACKUP_SECTIONS.some((section) => Array.isArray(file[section]))
  if (!hasSection) return { ok: false, reason: "no_sections" }

  return {
    ok: true,
    file: {
      format: typeof file.format === "string" ? file.format : BACKUP_FORMAT,
      version: typeof file.version === "number" ? file.version : 0,
      created_at: typeof file.created_at === "string" ? file.created_at : "",
      site: typeof file.site === "string" ? file.site : "",
      encrypted: typeof file.encrypted === "string" ? file.encrypted : "",
      settings: (file.settings ?? []) as SettingItem[],
      storages: (file.storages ?? []) as StorageWithDriver[],
      metas: (file.metas ?? []) as Meta[],
    },
  }
}

export type OpenFailure = ParseFailure | "wrong_password" | "corrupted"

export type OpenResult = { ok: true; payload: BackupPayload; encrypted: boolean } | { ok: false; reason: OpenFailure }

/** 解析 + （需要时）解密，产出可直接用于恢复的明文负载。 */
export function openBackup(text: string, password = ""): OpenResult {
  const parsed = parseBackupText(text)
  if (!parsed.ok) return parsed
  const file = parsed.file
  if (!file.encrypted) {
    // 明文文件带了口令也没关系：口令只用于解密，不解密就用不上
    return { ok: true, payload: pickPayload(file), encrypted: false }
  }
  if (!password) return { ok: false, reason: "wrong_password" }
  if (!verifyPassword(file, password)) return { ok: false, reason: "wrong_password" }

  const payload = emptyPayload()
  try {
    for (const section of BACKUP_SECTIONS) {
      const rows = file[section] as unknown as Record<string, unknown>[]
      payload[section] = rows.map((row) => {
        const out: Record<string, unknown> = {}
        for (const [field, value] of Object.entries(row)) {
          out[field] = decryptValue(String(value), password)
        }
        return out
      }) as never
    }
  } catch {
    // 口令校验过了但字段解不开：文件被手工改过或截断
    return { ok: false, reason: "corrupted" }
  }
  return { ok: true, payload, encrypted: true }
}

function pickPayload(file: BackupFile): BackupPayload {
  return {
    settings: file.settings ?? [],
    storages: file.storages ?? [],
    metas: file.metas ?? [],
  }
}

/** 恢复前给用户看的「这份文件里有什么」。 */
export function countBackup(payload: BackupPayload): Record<BackupSection, number> {
  return {
    settings: payload.settings.length,
    storages: payload.storages.length,
    metas: payload.metas.length,
  }
}

// ---------------------------------------------------------------------------
// 恢复：对账（新增 / 更新 / 跳过）
// ---------------------------------------------------------------------------

export interface ReconcilePlan<T> {
  /** 库里没有 → 新建。 */
  creates: T[]
  /** 库里有 → 覆盖模式才更新，否则进 skipped。 */
  updates: T[]
  /** 已存在但没开覆盖（或文件内重复）。 */
  skipped: T[]
}

/**
 * 按自然键对账。
 *
 * 自然键各域不同（settings.key / storages.mount_path / metas.path），
 * 这也是「恢复」区别于「整库导入」的地方：**永远不删**，只做新增与更新。
 * 文件内同键重复只保留第一条，其余算 skipped —— 不静默丢掉，也不让后一条覆盖前一条。
 */
export function reconcile<T>(
  incoming: readonly T[],
  current: readonly T[],
  keyOf: (item: T) => string,
  override: boolean,
): ReconcilePlan<T> {
  const existing = new Set(current.map(keyOf))
  const plan: ReconcilePlan<T> = { creates: [], updates: [], skipped: [] }
  const seen = new Set<string>()
  for (const item of incoming) {
    const key = keyOf(item)
    if (seen.has(key)) {
      plan.skipped.push(item)
      continue
    }
    seen.add(key)
    if (!existing.has(key)) plan.creates.push(item)
    else if (override) plan.updates.push(item)
    else plan.skipped.push(item)
  }
  return plan
}

/**
 * 设置项在恢复时要跳过的键。
 *
 * - `version`：写回旧版本号会让「是否需要升级」判断错乱；
 * - `index_progress`：索引进度属于本机状态，跨机恢复没有意义（旧前端同样过滤这两项）。
 */
export const SKIP_SETTING_KEYS = new Set(["version", "index_progress"])

export function restorableSettings(settings: readonly SettingItem[]): SettingItem[] {
  return settings.filter((item) => item.key && !SKIP_SETTING_KEYS.has(item.key))
}
