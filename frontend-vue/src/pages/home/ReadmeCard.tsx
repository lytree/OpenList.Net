import { computed, defineComponent, nextTick, ref, watch, type PropType } from "vue"
import { marked } from "marked"
import DOMPurify from "dompurify"
import { ElSkeleton } from "element-plus"
import { useSettingsStore } from "~/stores/settings"
import { downloadLink } from "~/utils/link"
import { pathJoin } from "~/utils/path"
import type { Obj } from "~/api/types"

/**
 * 目录 README 卡片。
 *
 * 取内容的优先级（对齐旧前端 `pages/home/Readme.tsx` + Go 的 meta 语义）：
 * 1. 当前目录里真实存在的 `readme.md` / `footer.md` / `bottom.md`（大小写不敏感）；
 * 2. 兜底用 `fs/list` 下发的 `readme` 字段 —— 那是 meta 的 readme，可能是
 *    一段 Markdown 原文，也可能是 http(s) 的 URL（远端 README）。
 *
 * 渲染管线与 Markdown 预览完全一致：marked → DOMPurify → v-html。
 * 内容来自用户文件与 meta，属不可信输入，必须消毒。
 */
const README_FILES = ["readme.md", "footer.md", "bottom.md"]
/** 单个 README 的取用上限：超过 1 MiB 基本不是给人读的说明文件。 */
const MAX_BYTES = 1024 * 1024

export default defineComponent({
  name: "ReadmeCard",
  props: {
    objs: { type: Array as PropType<Obj[]>, default: () => [] },
    readme: { type: String, default: "" },
    path: { type: String, default: "/" },
  },
  setup(props) {
    const settings = useSettingsStore()
    const enabled = computed(() => settings.getBool("readme_autorender"))

    /** 目录内命中的 README 文件（用于取内容 + 展示文件名）。 */
    const readmeFile = computed(() => {
      return props.objs.find(
        (obj) =>
          !obj.is_dir &&
          !obj.name.startsWith(".") &&
          README_FILES.includes(obj.name.toLowerCase()) &&
          obj.size <= MAX_BYTES,
      )
    })

    /** 远端/本地文本源；空串表示没有 README。 */
    const source = computed(() => {
      const file = readmeFile.value
      if (file) return downloadLink(pathJoin(props.path, file.name), file.sign)
      const raw = (props.readme ?? "").trim()
      if (!raw) return ""
      return /^https?:\/\//i.test(raw) ? raw : ""
    })

    /** meta 里直接写的 Markdown 原文（非 URL），不用发请求。 */
    const inlineText = computed(() => {
      const raw = (props.readme ?? "").trim()
      if (!raw || /^https?:\/\//i.test(raw)) return ""
      return raw
    })

    const text = ref("")
    const loading = ref(false)
    const failed = ref(false)

    async function load() {
      failed.value = false
      if (inlineText.value) {
        text.value = inlineText.value
        return
      }
      if (!source.value) {
        text.value = ""
        return
      }
      loading.value = true
      try {
        const resp = await fetch(source.value, { credentials: "omit" })
        text.value = resp.ok ? await resp.text() : ""
        failed.value = !resp.ok
      } catch {
        text.value = ""
        failed.value = true
      } finally {
        loading.value = false
      }
    }

    watch([source, inlineText], () => void load(), { immediate: true })

    const html = computed(() => {
      if (!text.value) return ""
      return DOMPurify.sanitize(marked.parse(text.value) as string, {
        USE_PROFILES: { html: true },
      })
    })

    const rootRef = ref<HTMLElement | null>(null)

    // 消毒后的外链统一新窗口打开，避免把 SPA 导航走。
    watch(html, async () => {
      await nextTick()
      rootRef.value?.querySelectorAll("a[href]").forEach((anchor) => {
        anchor.setAttribute("target", "_blank")
        anchor.setAttribute("rel", "noopener noreferrer")
      })
    })

    return () => {
      if (!enabled.value) return null
      if (loading.value) {
        return (
          <div class="ol-readme">
            <ElSkeleton rows={4} animated={true} />
          </div>
        )
      }
      if (failed.value) return null
      if (!html.value) return null
      return (
        <div class="ol-readme">
          <div class="ol-readme-head">
            <span class="ol-readme-title">{readmeFile.value?.name ?? "README"}</span>
          </div>
          <div ref={rootRef} class="ol-markdown ol-readme-body" v-html={html.value} />
        </div>
      )
    }
  },
})
