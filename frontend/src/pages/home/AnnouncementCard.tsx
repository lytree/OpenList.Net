import { computed, defineComponent } from "vue"
import { marked } from "marked"
import DOMPurify from "dompurify"
import { useSettingsStore } from "~/stores/settings"

/**
 * 站点公告卡片（设置项 `announcement`）。
 *
 * 位置：首页文件列表上方，与旧前端一致。内容按 Markdown 渲染 —— 管理员可能写
 * 纯文本、Markdown，也可能贴一段 HTML；三者都走 marked → DOMPurify 同一条消毒管线，
 * 不可信输入绝不直接 v-html。
 *
 * 为空时整个卡片不渲染（也就没有留白），这是「未配置公告」的默认观感。
 */
export default defineComponent({
  name: "AnnouncementCard",
  setup() {
    const settings = useSettingsStore()

    const html = computed(() => {
      const raw = settings.get("announcement").trim()
      if (!raw) return ""
      return DOMPurify.sanitize(marked.parse(raw, { async: false }) as string, {
        USE_PROFILES: { html: true },
      })
    })

    return () => {
      if (!html.value) return null
      return (
        <div class="ol-announcement">
          <div class="ol-markdown ol-announcement-body" v-html={html.value} />
        </div>
      )
    }
  },
})
