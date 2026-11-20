import { beforeEach, describe, expect, it } from "vitest"
import { mount } from "@vue/test-utils"
import { setLocale } from "~/i18n"
import DriverForm from "~/components/DriverForm"
import type { DriverInfoItem } from "~/api/types"

/**
 * 驱动配置表单的文案查找顺序：**驱动优先、`storages.common` 兜底、最后才是后端原文**。
 *
 * 之所以要钉这条：通用配置段（缓存有效期 / 网页代理 / WebDAV 策略 / 下载代理 / 签名 …）
 * 是后端 `StorageService.BuildCommonItems` 统一拼的，不属于任何驱动，`drivers.<driver>.*`
 * 里查不到它们。只查驱动那一层时，界面上就是一串 `cache_expiration` 之类的原始字段名 ——
 * 中文词条明明就写在 `storages.common` 里。
 */

const item = (name: string, extra: Partial<DriverInfoItem> = {}): DriverInfoItem =>
  ({ name, type: "string", ...extra }) as DriverInfoItem

function mountForm(items: DriverInfoItem[], driver = "") {
  return mount(DriverForm, {
    props: { items, driver, model: {}, onChange: () => {} },
  })
}

/** 表单标签文本（ElFormItem 的 label 渲染在 .el-form-item__label 里）。 */
const labels = (wrapper: ReturnType<typeof mountForm>) =>
  wrapper.findAll(".el-form-item__label").map((el) => el.text())

describe("驱动配置表单的中文文案", () => {
  beforeEach(() => setLocale("zh"))

  it("通用字段取 storages.common 的词条，而不是原始字段名", () => {
    const wrapper = mountForm([
      item("cache_expiration", { required: true }),
      item("disable_proxy_sign", { type: "bool" }),
      item("webdav_policy", { type: "select", options: "302_redirect,native_proxy" }),
    ])

    expect(labels(wrapper)).toEqual(["缓存有效期 *", "关闭代理签名", "WebDAV 策略"])
  })

  it("帮助文案同样兜底到 storages.common.<name>-tips", () => {
    const wrapper = mountForm([
      item("cache_expiration", { help: "The cache expiration time for this storage" }),
    ])

    expect(wrapper.find(".ol-field-help").text()).toBe("该存储的缓存有效期（分钟）")
  })

  it("驱动自有字段优先用 drivers.<driver>.<name>", () => {
    const wrapper = mountForm([item("directory_size", { help: "" })], "Local")

    expect(labels(wrapper)).toEqual(["目录大小"])
  })

  it("两层都查不到时回退后端原文（英文驱动字段不能变成空白）", () => {
    const wrapper = mountForm([item("some_unknown_field", { help: "raw help" })], "Local")

    expect(labels(wrapper)).toEqual(["some_unknown_field"])
    expect(wrapper.find(".ol-field-help").text()).toBe("raw help")
  })

  it("词条是空串表示「这一行不要帮助」，不退回英文", () => {
    // FTP 的 username/password 后端 help 只有 "Username"/"Password"，中文再重复一遍是噪音，
    // 所以词条写成 ""。这条钉住「空串 ≠ 缺词条」——退回英文就说明 translate() 的判定错了。
    const wrapper = mountForm([item("username", { help: "Username" })], "FTP")

    expect(labels(wrapper)).toEqual(["用户名"])
    expect(wrapper.find(".ol-field-help").exists()).toBe(false)
  })
})
