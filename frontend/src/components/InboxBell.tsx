import { defineComponent, onMounted, onUnmounted, ref } from "vue"
import { ElBadge, ElButton, ElDialog, ElEmpty, ElIcon, ElMessage } from "element-plus"
import { Bell } from "@element-plus/icons-vue"
import { markMessageRead, myMessages, myUnreadCount, type Message } from "~/api/messages"
import { formatTime } from "~/utils/format"
import { useUserStore } from "~/stores/user"
import { t } from "~/i18n"

/** 未读数轮询间隔。站内信没有推送通道（后端只有持久化存储），只能轮询。 */
const POLL_MS = 60_000

/**
 * 顶栏的消息铃铛 + 收件箱（见 docs/unified-media-product-plan.md §34）。
 *
 * 数据源是 `/me/message/list`、`/me/message/unread`、`/me/message/read`。
 * 未读数单独一个轻量接口，所以徽标可以低频轮询（60s）而不必每次拉全量列表；
 * 完整列表只在打开收件箱时才取。
 *
 * 「标为已读」就地改本地对象、同时把徽标减一，不重拉整表 —— 列表本身按 id 倒序、
 * 顺序不会因为已读状态变化而变，重拉纯属浪费，还会让列表闪一下。
 */
export default defineComponent({
  name: "InboxBell",
  setup() {
    const user = useUserStore()
    const unread = ref(0)
    const visible = ref(false)
    const loading = ref(false)
    const messages = ref<Message[]>([])
    let timer: number | undefined

    async function refreshUnread() {
      if (!user.isLogin) return
      const resp = await myUnreadCount()
      // 未读数拉不到（401/网络）不该弹错打扰用户：徽标保持上一次的值即可
      if (resp.code === 200) unread.value = resp.data ?? 0
    }

    async function openInbox() {
      visible.value = true
      loading.value = true
      const resp = await myMessages()
      loading.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      messages.value = resp.data ?? []
    }

    async function markRead(msg: Message) {
      if (msg.read) return
      const resp = await markMessageRead(msg.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      msg.read = true
      unread.value = Math.max(0, unread.value - 1)
    }

    onMounted(() => {
      void refreshUnread()
      timer = window.setInterval(() => void refreshUnread(), POLL_MS)
    })
    onUnmounted(() => window.clearInterval(timer))

    return () =>
      user.isLogin ? (
        <>
          <ElBadge
            value={unread.value}
            hidden={unread.value === 0}
            max={99}
            class="ol-inbox-badge"
          >
            {/* title 只能挂在原生元素上：ElButton 的类型里没有它（本仓库其它图标按钮同样用 span 包裹） */}
            <span title={t("inbox.bell")}>
              <ElButton text={true} circle={true} onClick={() => void openInbox()}>
                <ElIcon>
                  <Bell />
                </ElIcon>
              </ElButton>
            </span>
          </ElBadge>

          <ElDialog
            modelValue={visible.value}
            title={t("inbox.title")}
            width="640px"
            onUpdate:modelValue={(v: boolean) => (visible.value = v)}
          >
            {{
              default: () => (
                <div class="ol-inbox-list" v-loading={loading.value}>
                  {messages.value.length === 0 && !loading.value && (
                    <ElEmpty description={t("inbox.empty")} />
                  )}
                  {messages.value.map((m) => (
                    <article key={m.id} class={["ol-inbox-item", m.read ? "is-read" : "is-unread"]}>
                      <header class="ol-inbox-item-head">
                        {!m.read && <span class="ol-inbox-dot" />}
                        <span class="ol-inbox-item-title">{m.title}</span>
                        <span class="ol-field-help">{formatTime(m.createdAt)}</span>
                        {!m.read && (
                          <ElButton size="small" text={true} onClick={() => void markRead(m)}>
                            {t("inbox.mark_read")}
                          </ElButton>
                        )}
                      </header>
                      <p class="ol-inbox-item-body">{m.content}</p>
                    </article>
                  ))}
                </div>
              ),
              footer: () => (
                <div class="ol-manage-form-actions">
                  <ElButton onClick={() => void openInbox()} loading={loading.value}>
                    {t("inbox.refresh")}
                  </ElButton>
                  <ElButton type="primary" onClick={() => (visible.value = false)}>
                    {t("inbox.close")}
                  </ElButton>
                </div>
              ),
            }}
          </ElDialog>
        </>
      ) : null
  },
})
