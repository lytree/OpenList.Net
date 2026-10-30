import { defineComponent, computed, ref, onMounted } from "vue"
import { useRoute } from "vue-router"
import { ElAlert, ElButton, ElCard, ElIcon, ElInput, ElTag } from "element-plus"
import { VexTable, VexColumn } from "~/components/table"
import { ArrowLeft, Download } from "@element-plus/icons-vue"
import { shareDownloadLink, shareFsGet, shareFsList } from "~/api/sharePublic"
import type { FsGetData, Obj } from "~/api/types"
import { useSettingsStore } from "~/stores/settings"
import { humanSize, formatTime } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 公开分享页（`#/s/:code`）。
 *
 * 它与主应用完全隔离：不带登录 token、不用 BasicLayout，只靠 `Authorization: <shareCode>`
 * 访问 `/fs/get`、`/fs/list` 与 `/sd` 下载。后端把分享码解析成 `BasePath = 分享路径` 的
 * 受限访客，所以这里的路径**一律是相对分享根的**：`/` 就是分享根本身。
 *
 * 三种形态：
 * - 分享的是目录 → 面包屑 + 文件列表，点目录进入、点文件下载；
 * - 分享的是单个文件 → 直接给一张下载卡片；
 * - 分享带密码 → 先出密码框，`/fs/get` 返回 403 时自动切过去。
 */
export default defineComponent({
  name: "SharePage",
  setup() {
    const route = useRoute()
    const settings = useSettingsStore()
    const code = computed(() => String(route.params.code ?? ""))

    const loading = ref(false)
    const error = ref("")
    const needPwd = ref(false)
    const pwd = ref("")
    const pwdInput = ref("")
    const entries = ref<Obj[]>([])
    const currentPath = ref("/")
    const rootFile = ref<FsGetData | null>(null)

    const sorted = computed(() =>
      [...entries.value].sort((a, b) =>
        a.is_dir === b.is_dir ? a.name.localeCompare(b.name) : a.is_dir ? -1 : 1,
      ),
    )

    const crumbs = computed(() => {
      const list = [{ label: t("shares.root"), path: "/" }]
      let acc = ""
      for (const part of currentPath.value.split("/").filter(Boolean)) {
        acc += `/${part}`
        list.push({ label: part, path: acc })
      }
      return list
    })

    /**
     * 只有「密码错/缺密码」才回到密码框；已禁用、已过期、超次数、不存在都必须直接给出原因，
     * 否则访客会陷在密码框里反复试（后端这几类错误同样是 403）。
     */
    function isPasswordIssue(message: string) {
      return /password/i.test(message)
    }

    /** 后端这几类是固定英文文案，映射成可读提示（命中不了就原样展示）。 */
    const FRIENDLY: Record<string, string> = {
      "sharing disabled": "shares.share_disabled",
      "sharing expired": "shares.share_expired",
      "sharing max accessed": "shares.share_exhausted",
      "sharing not found": "shares.not_found",
    }

    function friendly(message?: string): string {
      const key = FRIENDLY[(message ?? "").trim().toLowerCase()]
      return key ? t(key) : message || t("shares.not_found")
    }

    async function enter(path: string) {
      loading.value = true
      const resp = await shareFsList(code.value, path, pwd.value || undefined)
      loading.value = false
      if (resp.code === 403 && isPasswordIssue(resp.message ?? "")) {
        needPwd.value = true
        error.value = pwd.value ? t("shares.wrong_password") : ""
        return
      }
      if (resp.code !== 200) {
        error.value = friendly(resp.message)
        return
      }
      error.value = ""
      entries.value = resp.data?.content ?? []
      currentPath.value = path
    }

    async function boot() {
      loading.value = true
      error.value = ""
      const resp = await shareFsGet(code.value, "/", pwd.value || undefined)
      loading.value = false
      if (resp.code === 403 && isPasswordIssue(resp.message ?? "")) {
        needPwd.value = true
        if (pwd.value) error.value = t("shares.wrong_password")
        return
      }
      if (resp.code !== 200) {
        error.value = friendly(resp.message)
        return
      }
      needPwd.value = false
      if (resp.data?.is_dir) {
        rootFile.value = null
        await enter("/")
      } else {
        rootFile.value = resp.data
      }
    }

    function onPwdSubmit() {
      pwd.value = pwdInput.value
      // 记住密码：同一标签页内刷新/切面包屑不再重复询问（关标签即失效）
      try {
        sessionStorage.setItem(`share-pwd:${code.value}`, pwd.value)
      } catch {
        /* 隐私模式可能禁用 sessionStorage */
      }
      void boot()
    }

    function link(relPath: string) {
      return shareDownloadLink(code.value, relPath, pwd.value || undefined)
    }

    function entryPath(obj: Obj) {
      return `${currentPath.value.replace(/\/$/, "")}/${obj.name}`
    }

    onMounted(() => {
      try {
        const saved = sessionStorage.getItem(`share-pwd:${code.value}`)
        if (saved) pwd.value = saved
      } catch {
        /* ignore */
      }
      void boot()
    })

    function body() {
      if (needPwd.value) {
        return (
          <div class="ol-share-gate">
            <ElAlert
              type="warning"
              showIcon={true}
              closable={false}
              title={t("shares.password_required")}
            />
            {/* 回车提交：ElInput 没有 onKeyup prop，用原生容器代理键盘事件 */}
            <div
              class="ol-share-gate-row"
              onKeyup={(e: KeyboardEvent) => {
                if (e.key === "Enter") onPwdSubmit()
              }}
            >
              <ElInput
                type="password"
                showPassword={true}
                placeholder={t("shares.enter_password")}
                modelValue={pwdInput.value}
                onUpdate:modelValue={(v: string) => (pwdInput.value = v)}
              />
              <ElButton type="primary" loading={loading.value} onClick={onPwdSubmit}>
                {t("global.confirm")}
              </ElButton>
            </div>
            {error.value && <div class="ol-share-error">{error.value}</div>}
          </div>
        )
      }

      if (error.value) {
        return <ElAlert type="error" showIcon={true} closable={false} title={error.value} />
      }

      if (rootFile.value) {
        return (
          <div class="ol-share-single">
            <div class="ol-share-file-name">{rootFile.value.name}</div>
            <div class="ol-field-help">
              {humanSize(rootFile.value.size)} · {formatTime(rootFile.value.modified)}
            </div>
            <a href={link("/")} target="_blank" rel="noopener">
              <ElButton type="primary">
                <ElIcon>
                  <Download />
                </ElIcon>
                {t("shares.download")}
              </ElButton>
            </a>
          </div>
        )
      }

      return (
        <>
          <div class="ol-share-crumbs">
            {crumbs.value.map((c, i) => (
              <span key={c.path}>
                {i > 0 && <span class="ol-muted"> / </span>}
                {i === crumbs.value.length - 1 ? (
                  <span>{c.label}</span>
                ) : (
                  <a href="javascript:void(0)" onClick={() => void enter(c.path)}>
                    {c.label}
                  </a>
                )}
              </span>
            ))}
          </div>
          <VexTable data={sorted.value} size="small" style={{ width: "100%" }}>
            <VexColumn label={t("global.name")} minWidth={240}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as Obj
                  return r.is_dir ? (
                    <a
                      href="javascript:void(0)"
                      class="ol-share-entry"
                      onClick={() => void enter(entryPath(r))}
                    >
                      {r.name}/
                    </a>
                  ) : (
                    <span class="ol-share-entry">{r.name}</span>
                  )
                },
              }}
            </VexColumn>
            <VexColumn label={t("shares.size")} width={120}>
              {{
                default: ({ row }) => {
                  const r = row as unknown as Obj
                  return r.is_dir ? <span class="ol-muted">-</span> : humanSize(r.size)
                },
              }}
            </VexColumn>
            <VexColumn label={t("shares.modified")} width={170}>
              {{
                default: ({ row }) => (
                  <span class="ol-field-help">{formatTime((row as unknown as Obj).modified)}</span>
                ),
              }}
            </VexColumn>
            <VexColumn label={t("global.operations")} width={120} align="center">
              {{
                default: ({ row }) => {
                  const r = row as unknown as Obj
                  if (r.is_dir) return <span class="ol-muted">-</span>
                  return (
                    <a href={link(entryPath(r))} target="_blank" rel="noopener">
                      <ElButton size="small" text={true} type="primary">
                        <ElIcon>
                          <Download />
                        </ElIcon>
                      </ElButton>
                    </a>
                  )
                },
              }}
            </VexColumn>
          </VexTable>
          {!sorted.value.length && (
            <div class="ol-share-empty">{t("shares.empty_dir")}</div>
          )}
        </>
      )
    }

    return () => (
      <div class="ol-share-page">
        <div class="ol-share-topbar">
          <div class="ol-share-brand">
            <span class="ol-share-brand-mark">
              {(settings.siteTitle || "O").slice(0, 1)}
            </span>
            <span class="ol-share-brand-name">{settings.siteTitle}</span>
          </div>
          <ElTag size="small" type="info">
            {t("shares.shared_by_link")}
          </ElTag>
        </div>

        <ElCard shadow="never" class="ol-share-card">
          <div class="ol-share-head">
            <h1 class="ol-share-title">{t("shares.title")}</h1>
            <ElButton
              text={true}
              size="small"
              onClick={() => {
                window.location.href = `${location.origin}/#/`
              }}
            >
              <ElIcon>
                <ArrowLeft />
              </ElIcon>
              {t("shares.back_home")}
            </ElButton>
          </div>
          {loading.value && !entries.value.length && !rootFile.value ? (
            <div class="ol-share-empty">…</div>
          ) : (
            body()
          )}
        </ElCard>
        <div class="ol-share-footer">
          {t("shares.powered_by")} {settings.siteTitle}
        </div>
      </div>
    )
  },
})
