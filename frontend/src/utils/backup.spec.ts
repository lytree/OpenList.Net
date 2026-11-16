import { createCipheriv, createDecipheriv, createHash, randomBytes } from "node:crypto"
import { describe, expect, it } from "vitest"
import type { Meta, SettingItem, StorageWithDriver } from "~/api/types"
import {
  BackupCryptoError,
  backupFileName,
  countBackup,
  decryptValue,
  emptyPayload,
  encryptValue,
  openBackup,
  parseBackupText,
  reconcile,
  restorableSettings,
  sealBackup,
  serializeBackup,
  verifyPassword,
} from "./backup"

const setting = (patch: Partial<SettingItem> = {}): SettingItem => ({
  key: "site_title",
  value: "OpenList",
  help: null,
  type: "string",
  flag: 0,
  options: null,
  group: 1,
  ...patch,
})

const storage = (patch: Partial<StorageWithDriver> = {}): StorageWithDriver =>
  ({
    id: 1,
    mount_path: "/local",
    driver: "Local",
    driver_name: "Local",
    addition: '{"root_folder_path":"D:/x"}',
    order: 0,
    disabled: false,
    status: "work",
    web_proxy: false,
    enable_sign: false,
    modified: "",
    ...patch,
  }) as StorageWithDriver

const meta = (patch: Partial<Meta> = {}): Meta => ({
  id: 1,
  path: "/local/secret",
  p_sub: false,
  read_users: [1, 2],
  read_users_sub: false,
  write_users: [],
  write_users_sub: false,
  write: false,
  w_sub: false,
  h_sub: false,
  r_sub: false,
  header_sub: false,
  ...patch,
})

/** 一份完整的样本负载。 */
function samplePayload() {
  return {
    settings: [setting(), setting({ key: "version", value: "v1" })],
    storages: [storage()],
    metas: [meta()],
  }
}

// ---------------------------------------------------------------------------
// CryptoJS 兼容性：用 Node 的 crypto 独立实现一遍 OpenSSL 口令格式来对拍
// ---------------------------------------------------------------------------

/** CryptoJS 的 `AES.encrypt(passphrase)`：Salted__ + 8 字节盐 + AES-256-CBC(PKCS7)，MD5 派生。 */
function cryptoJsCompatibleEncrypt(plain: string, passphrase: string): string {
  const salt = randomBytes(8)
  const material = Buffer.concat([Buffer.from(passphrase, "utf8")])
  let derived = Buffer.alloc(0)
  let block = Buffer.alloc(0)
  while (derived.length < 48) {
    block = createHash("md5").update(Buffer.concat([block, material, salt])).digest()
    derived = Buffer.concat([derived, block])
  }
  const key = derived.subarray(0, 32)
  const iv = derived.subarray(32, 48)
  const cipher = createCipheriv("aes-256-cbc", key, iv)
  const body = Buffer.concat([cipher.update(plain, "utf8"), cipher.final()])
  return Buffer.concat([Buffer.from("Salted__", "ascii"), salt, body]).toString("base64")
}

function cryptoJsCompatibleDecrypt(encoded: string, passphrase: string): string {
  const raw = Buffer.from(encoded, "base64")
  expect(raw.subarray(0, 8).toString("ascii")).toBe("Salted__")
  const salt = raw.subarray(8, 16)
  const body = raw.subarray(16)
  const material = Buffer.from(passphrase, "utf8")
  let derived = Buffer.alloc(0)
  let block = Buffer.alloc(0)
  while (derived.length < 48) {
    block = createHash("md5").update(Buffer.concat([block, material, salt])).digest()
    derived = Buffer.concat([derived, block])
  }
  const decipher = createDecipheriv(
    "aes-256-cbc",
    derived.subarray(0, 32),
    derived.subarray(32, 48),
  )
  return Buffer.concat([decipher.update(body), decipher.final()]).toString("utf8")
}

/** 拆开双 base64 的外层，拿到内层的 CryptoJS 密文串。 */
const innerCipher = (encoded: string) => Buffer.from(encoded, "base64").toString("utf8")

describe("加密格式与旧前端（CryptoJS）互通", () => {
  it("我们加密的字段能被独立实现（Node crypto）解开", () => {
    const encoded = encryptValue("站点标题", "hunter2")
    const plain = cryptoJsCompatibleDecrypt(innerCipher(encoded), "hunter2")
    expect(JSON.parse(plain)).toBe("站点标题")
  })

  it("独立实现加密的字段能被我们解开（旧备份里就是这个形状）", () => {
    const cipher = cryptoJsCompatibleEncrypt(JSON.stringify({ a: 1, b: ["x"] }), "hunter2")
    const doubleBase64 = Buffer.from(cipher, "utf8").toString("base64")
    expect(decryptValue(doubleBase64, "hunter2")).toEqual({ a: 1, b: ["x"] })
  })

  it("数字与布尔值往返后仍是原来的类型（字段是 JSON 序列化的）", () => {
    const encoded = encryptValue(42, "pw")
    expect(decryptValue(encoded, "pw")).toBe(42)
    expect(decryptValue(encryptValue(true, "pw"), "pw")).toBe(true)
  })

  it("口令错时抛 BackupCryptoError", () => {
    const encoded = encryptValue("secret", "right")
    expect(() => decryptValue(encoded, "wrong")).toThrow(BackupCryptoError)
  })

  it("同一字段两次加密的密文不同（随机盐），但都能解开", () => {
    const a = encryptValue("same", "pw")
    const b = encryptValue("same", "pw")
    expect(a).not.toBe(b)
    expect(decryptValue(a, "pw")).toBe("same")
    expect(decryptValue(b, "pw")).toBe("same")
  })
})

describe("verifyPassword", () => {
  it("明文备份不需要口令", () => {
    expect(verifyPassword({ encrypted: "" }, "")).toBe(true)
  })

  it("加密备份用对口令返回 true，错口令返回 false", () => {
    const file = sealBackup(samplePayload(), { password: "pw" })
    expect(file.encrypted).not.toBe("")
    expect(verifyPassword(file, "pw")).toBe(true)
    expect(verifyPassword(file, "nope")).toBe(false)
  })
})

describe("sealBackup / openBackup", () => {
  it("明文往返：三个数组原样回来，并带上格式头", () => {
    const payload = samplePayload()
    const file = sealBackup(payload, { site: "My Site", now: new Date("2026-10-27T03:04:05Z") })
    expect(file.format).toBe("openlist-net-backup")
    expect(file.site).toBe("My Site")
    expect(file.encrypted).toBe("")

    const opened = openBackup(serializeBackup(file))
    expect(opened.ok).toBe(true)
    if (!opened.ok) return
    expect(opened.payload.settings).toHaveLength(2)
    expect(opened.payload.storages[0]!.mount_path).toBe("/local")
    expect(opened.payload.metas[0]!.read_users).toEqual([1, 2])
  })

  it("加密往返：数组内容全部还原，且文件里看不到明文", () => {
    const file = sealBackup(samplePayload(), { password: "pw" })
    const text = serializeBackup(file)
    // 明文不能出现在文件里（连设置值都不能），否则加密就是摆设
    expect(text).not.toContain("OpenList")
    expect(text).not.toContain("/local/secret")

    const opened = openBackup(text, "pw")
    expect(opened.ok).toBe(true)
    if (!opened.ok) return
    expect(opened.encrypted).toBe(true)
    expect(opened.payload.settings[0]!.value).toBe("OpenList")
    expect(opened.payload.storages[0]!.addition).toBe('{"root_folder_path":"D:/x"}')
    expect(opened.payload.metas[0]!.path).toBe("/local/secret")
  })

  it("加密文件不给口令 / 给错口令都失败，且原因是 wrong_password", () => {
    const text = serializeBackup(sealBackup(samplePayload(), { password: "pw" }))
    expect(openBackup(text)).toEqual({ ok: false, reason: "wrong_password" })
    expect(openBackup(text, "bad")).toEqual({ ok: false, reason: "wrong_password" })
  })

  it("密文被篡改 → corrupted（口令标记还能过，字段解不开）", () => {
    const file = sealBackup(samplePayload(), { password: "pw" })
    // 把某个字段的密文截断一半：sentinel 不动，字段必炸
    file.settings[0] = { ...file.settings[0]!, value: file.settings[0]!.value.slice(0, 20) }
    expect(openBackup(serializeBackup(file), "pw")).toEqual({ ok: false, reason: "corrupted" })
  })
})

describe("parseBackupText", () => {
  it("非 JSON / 非对象 / 没有任何数组 都有明确原因", () => {
    expect(parseBackupText("not json")).toEqual({ ok: false, reason: "invalid_json" })
    expect(parseBackupText("[1,2]")).toEqual({ ok: false, reason: "not_a_backup" })
    expect(parseBackupText('{"foo":1}')).toEqual({ ok: false, reason: "no_sections" })
  })

  it("旧前端的文件（没有 format/version）也能认出来，缺失数组补空", () => {
    const legacy = JSON.stringify({
      encrypted: "",
      settings: [],
      storages: [{ mount_path: "/old" }],
      users: [{ id: 1, username: "a" }],
    })
    const parsed = parseBackupText(legacy)
    expect(parsed.ok).toBe(true)
    if (!parsed.ok) return
    expect(parsed.file.version).toBe(0)
    expect(parsed.file.storages).toHaveLength(1)
    expect(parsed.file.metas).toEqual([])
    // 用户数组不再属于备份格式，恢复时被忽略
    expect("users" in parsed.file).toBe(false)
  })
})

describe("reconcile", () => {
  const byKey = (item: SettingItem) => item.key

  it("库里没有的新建，库里有的在覆盖模式下更新", () => {
    const plan = reconcile(
      [setting(), setting({ key: "logo", value: "/logo.png" })],
      [setting({ value: "old" })],
      byKey,
      true,
    )
    expect(plan.creates.map(byKey)).toEqual(["logo"])
    expect(plan.updates.map(byKey)).toEqual(["site_title"])
    expect(plan.skipped).toEqual([])
  })

  it("不开覆盖时已存在的进 skipped（不是错误）", () => {
    const plan = reconcile([setting({ value: "new" })], [setting({ value: "old" })], byKey, false)
    expect(plan.creates).toEqual([])
    expect(plan.updates).toEqual([])
    expect(plan.skipped.map(byKey)).toEqual(["site_title"])
  })

  it("文件内同键重复只保留第一条，其余进 skipped", () => {
    const plan = reconcile(
      [setting({ value: "a" }), setting({ value: "b" })],
      [],
      byKey,
      true,
    )
    expect(plan.creates.map((s) => s.value)).toEqual(["a"])
    expect(plan.skipped.map((s) => s.value)).toEqual(["b"])
  })

  it("空文件 / 空库不出错", () => {
    expect(reconcile([], [setting()], byKey, true)).toEqual({ creates: [], updates: [], skipped: [] })
  })
})

describe("backupFileName", () => {
  const at = new Date(2026, 9, 27, 11, 30, 5) // 本地时间 2026-10-27 11:30:05

  it("时间戳可排序且不含路径分隔符", () => {
    expect(backupFileName("My Site", at)).toBe("openlist_backup_My_Site_20261027-113005.json")
  })

  it("站点名里的非法字符被压成下划线，空站点回退 openlist", () => {
    expect(backupFileName('a/b:c*?"<>|d', at)).toBe("openlist_backup_a_b_c_d_20261027-113005.json")
    expect(backupFileName("", at)).toBe("openlist_backup_openlist_20261027-113005.json")
  })
})

describe("restorableSettings", () => {
  it("过滤 version / index_progress（本机状态，写回会出问题）", () => {
    const out = restorableSettings([
      setting(),
      setting({ key: "version" }),
      setting({ key: "index_progress" }),
    ])
    expect(out.map((s) => s.key)).toEqual(["site_title"])
  })
})

describe("countBackup", () => {
  it("统计各类条目数", () => {
    expect(countBackup(samplePayload())).toEqual({
      settings: 2,
      storages: 1,
      metas: 1,
    })
    expect(countBackup(emptyPayload())).toEqual({
      settings: 0,
      storages: 0,
      metas: 0,
    })
  })
})
