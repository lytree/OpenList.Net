import { computed, defineComponent, onMounted, ref } from "vue"
import { ElAlert, ElButton, ElCard, ElEmpty, ElIcon, ElTag } from "element-plus"
import { Refresh } from "@element-plus/icons-vue"
import { systemInfo, type SystemInfo } from "~/api/system"
import { formatTime } from "~/utils/format"
import {
  capabilityTags,
  clockSkewSeconds,
  formatMaxConnections,
  formatSkew,
  formatUptime,
  shortCommit,
} from "~/utils/about"
import { t } from "~/i18n"

/**
 * 一行「键 → 值」。定义在模块级而不是 setup 里：放在 setup 里每次渲染都会造出一个
 * 新的组件类型，Vue 会当成不同组件卸载重建（`v-loading` 一变整块表格就重挂）。
 * 值优先用 `title` 兜住长路径：路径在窄屏一定被省略号截断，但悬停要能看到全貌。
 */
const AboutRow = defineComponent({
  name: "AboutRow",
  props: {
    k: { type: String, default: "" },
    v: { type: String, default: "" },
    tip: { type: String, default: "" },
    path: { type: Boolean, default: false },
  },
  setup(props) {
    return () => (
      <div class="ol-about-row">
        <span class="ol-about-key">{props.k}</span>
        <span class={["ol-about-val", props.path ? "is-path" : ""]} title={props.tip || props.v}>
          {props.v || "-"}
        </span>
      </div>
    )
  },
})

/**
 * 管理台 · 关于（见 docs/unified-media-product-plan.md §38）。
 *
 * 与上游的差别写在这里：旧前端这一页是**去 GitHub 拉 README 再渲染**（`fetch(README.md)`），
 * 离线部署直接是一片空白，而且它回答不了运维真正会问的两个问题 ——
 * 「这台机器跑的是哪个版本」和「这版前端配的是哪个后端」。
 * 所以本页读 `GET /api/admin/system/info`：版本/提交/构建时间、运行时与操作系统、
 * 数据库与各类路径、已启用的内置服务。
 *
 * 三条实现约束：
 * 1. **只读**：没有任何写操作，刷新是唯一的交互；
 * 2. **取不到就显示 `-`**：字段可能是空串（未注入版本、单文件发布取不到程序集时间、
 *    Postgres 没有数据目录），别把 `""` 渲染成 `1970-01-01` 或 `NaN`；
 * 3. 时钟偏差是**页面现算**的（服务器时间 vs 本机），不进缓存、不做轮询 ——
 *    它只是「一眼看出两台机器的钟差多少」的提示。
 */
export default defineComponent({
  name: "AboutPage",
  setup() {
    const info = ref<SystemInfo | null>(null)
    const loading = ref(true)
    const error = ref("")

    const commitShort = computed(() => shortCommit(info.value?.commit))
    const uptime = computed(() => formatUptime(info.value?.uptime_seconds))
    const skew = computed(() => clockSkewSeconds(info.value?.server_time))
    const capabilities = computed(() => capabilityTags(info.value?.features))
    const dbLocation = computed(() => {
      const db = info.value?.database
      if (!db) return "-"
      if (db.type === "sqlite") return db.file || "-"
      // Postgres：口令永不返回，这里只拼「主机:端口/库名」
      return `${db.host || "-"}:${db.port || "-"}/${db.name || "-"}`
    })

    async function load() {
      loading.value = true
      error.value = ""
      const resp = await systemInfo()
      loading.value = false
      if (resp.code !== 200 || !resp.data) {
        error.value = resp.message || t("about.load_failed")
        return
      }
      info.value = resp.data
    }

    onMounted(() => void load())

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never" v-loading={loading.value}>
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("about.title")}</h2>
            <div class="ol-manage-actions">
              <ElButton size="small" loading={loading.value} onClick={() => void load()}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("global.refresh")}
              </ElButton>
            </div>
          </div>

          {error.value && (
            <ElAlert
              type="error"
              showIcon={true}
              closable={false}
              class="ol-about-alert"
              title={error.value}
            />
          )}

          {!info.value && !loading.value && !error.value && (
            <ElEmpty description={t("about.load_failed")} />
          )}

          {info.value && (
            <>
              <section class="ol-about-hero">
                <div class="ol-about-hero-main">
                  <span class="ol-about-tag">{info.value.tag}</span>
                  {commitShort.value && (
                    <span class="ol-about-commit" title={info.value.commit || undefined}>
                      #{commitShort.value}
                    </span>
                  )}
                  <ElTag size="small" type="info">
                    {info.value.configuration || "-"}
                  </ElTag>
                </div>
                <p class="ol-about-sub">{t("about.subtitle")}</p>
              </section>

              <div class="ol-about-grid">
                <section class="ol-about-block">
                  <h3 class="ol-about-h3">{t("about.section_runtime")}</h3>
                  {/* 版本与提交在顶部横幅里 —— 这里再列一遍只会让人以为有两套版本号 */}
                  <AboutRow k={t("about.row_runtime")} v={info.value.runtime} />
                  <AboutRow k={t("about.row_os")} v={info.value.os} />
                  <AboutRow
                    k={t("about.row_arch")}
                    v={`${info.value.architecture || "-"} · ${info.value.processor_count || 0} ${t("about.row_cores_suffix")}`}
                  />
                  <AboutRow
                    k={t("about.row_started_at")}
                    v={formatTime(info.value.started_at, "-")}
                  />
                  <AboutRow k={t("about.row_uptime")} v={uptime.value} tip={t("about.tip_uptime")} />
                  <AboutRow
                    k={t("about.row_skew")}
                    v={formatSkew(skew.value)}
                    tip={t("about.tip_skew")}
                  />
                </section>

                <section class="ol-about-block">
                  <h3 class="ol-about-h3">{t("about.section_database")}</h3>
                  <AboutRow k={t("about.row_db_type")} v={info.value.database?.type || "-"} />
                  <AboutRow
                    k={t("about.row_db_location")}
                    v={dbLocation.value}
                    tip={dbLocation.value}
                    path={true}
                  />
                </section>
              </div>

              {/* 路径块单独占满整行：在 340px 的两列里，Windows 绝对路径只能露出
                  `F:\Code\Dotnet\OpenList.Net...`，等于没给信息（完整值虽有 title，
                  但排查问题时不该要求用户先悬停）。 */}
              <section class="ol-about-block">
                <h3 class="ol-about-h3">{t("about.section_paths")}</h3>
                <AboutRow
                  k={t("about.row_data_dir")}
                  v={info.value.paths?.data_dir || "-"}
                  path={true}
                />
                <AboutRow
                  k={t("about.row_temp_dir")}
                  v={info.value.paths?.temp_dir || "-"}
                  path={true}
                />
                <AboutRow
                  k={t("about.row_log")}
                  v={
                    info.value.paths?.log_enabled
                      ? info.value.paths.log_file || "-"
                      : t("about.log_disabled")
                  }
                  tip={info.value.paths?.log_file}
                  path={info.value.paths?.log_enabled === true}
                />
                <AboutRow
                  k={t("about.row_dist_dir")}
                  v={info.value.paths?.dist_dir || "-"}
                  path={true}
                />
                <AboutRow
                  k={t("about.row_built_at")}
                  v={formatTime(info.value.built_at, "-")}
                  tip={t("about.tip_built_at")}
                />
                <AboutRow
                  k={t("about.row_dist_built_at")}
                  v={formatTime(info.value.paths?.dist_built_at, "-")}
                  tip={t("about.tip_dist_built_at")}
                />
              </section>

              <section class="ol-about-block">
                <h3 class="ol-about-h3">{t("about.section_features")}</h3>
                <div class="ol-about-tags">
                  {capabilities.value.map((c) => (
                    <ElTag key={c.key} size="small" type={c.enabled ? "success" : "info"}>
                      {c.label}
                      {" · "}
                      {c.enabled ? t("about.state_on") : t("about.state_off")}
                    </ElTag>
                  ))}
                </div>
                <AboutRow
                  k={t("about.row_search_index")}
                  v={info.value.features?.search_index || "-"}
                />
                <AboutRow
                  k={t("about.row_max_conn")}
                  v={formatMaxConnections(info.value.features?.max_connections)}
                  tip={t("about.tip_max_conn")}
                />
              </section>

              <section class="ol-about-block">
                <h3 class="ol-about-h3">{t("about.section_links")}</h3>
                <div class="ol-about-links">
                  {[
                    ["about.link_repo", "https://github.com/lytree/OpenList.Net"],
                    ["about.link_issues", "https://github.com/lytree/OpenList.Net/issues"],
                    ["about.link_releases", "https://github.com/lytree/OpenList.Net/releases"],
                    ["about.link_upstream", "https://github.com/OpenListTeam/OpenList"],
                  ].map(([key, href]) => (
                    <a key={key} href={href} target="_blank" rel="noopener noreferrer">
                      {t(key)}
                    </a>
                  ))}
                </div>
                <p class="ol-about-foot">{t("about.footer")}</p>
              </section>
            </>
          )}
        </ElCard>
      </div>
    )
  },
})
