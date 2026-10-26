import { computed, defineComponent, onMounted, ref } from "vue"
import {
  ElAlert,
  ElButton,
  ElCard,
  ElEmpty,
  ElIcon,
  ElInput,
  ElMessage,
  ElMessageBox,
  ElTag,
} from "element-plus"
import { Delete, Promotion, Refresh, Search } from "@element-plus/icons-vue"
import { listUsers } from "~/api/admin"
import {
  adminCreateMessage,
  adminDeleteMessage,
  adminListMessages,
  type Message,
} from "~/api/messages"
import type { AdminUser } from "~/api/types"
import { formatTime } from "~/utils/format"
import { t } from "~/i18n"

/**
 * 管理台 · 站内信（见 docs/unified-media-product-plan.md §34）。
 *
 * 这一页对应后端 `AdminEndpoints.cs` 的 `/admin/message/{list,create,delete}`：
 * 给**指定用户**发持久化消息，用户在自己那边的收件箱里看到（`/me/message/*`）。
 *
 * 刻意**不做**「广播」面板：`POST /admin/message/send` + `POST /admin/message/get`
 * 那一对是 Go 版的调试回显工具（消息只在内存里留最新一条，用户侧没有任何消费端）。
 * 旧前端的 Messenger 就是每秒轮询它、把收到的追加到列表里 —— 那是自测工具，不是功能。
 * 要做真正的全站公告得先有落库语义，见文档 §34.6。
 *
 * 契约要点：
 * - 本域是 **camelCase**（`userId` / `createdAt`），与媒体域的 snake_case 不同；
 * - `GET /admin/user/list` 不分页，一次返回全部用户，直接当选择器用。
 */
export default defineComponent({
  name: "MessengerPage",
  setup() {
    const users = ref<AdminUser[]>([])
    const usersLoading = ref(false)
    const keyword = ref("")
    const selectedId = ref<number | null>(null)

    const messages = ref<Message[]>([])
    const messagesLoading = ref(false)

    const title = ref("")
    const content = ref("")
    const sending = ref(false)

    const filteredUsers = computed(() => {
      const kw = keyword.value.trim().toLowerCase()
      if (!kw) return users.value
      return users.value.filter((u) => u.username.toLowerCase().includes(kw))
    })

    const selectedUser = computed(() => users.value.find((u) => u.id === selectedId.value) ?? null)

    const unreadCount = computed(() => messages.value.filter((m) => !m.read).length)

    async function loadUsers() {
      usersLoading.value = true
      const resp = await listUsers()
      usersLoading.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      users.value = resp.data?.content ?? []
      if (selectedId.value === null && users.value.length > 0) {
        await selectUser(users.value[0].id)
      }
    }

    async function selectUser(id: number) {
      selectedId.value = id
      await loadMessages()
    }

    async function loadMessages() {
      if (selectedId.value === null) return
      messagesLoading.value = true
      const resp = await adminListMessages(selectedId.value)
      messagesLoading.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        messages.value = []
        return
      }
      messages.value = resp.data ?? []
    }

    async function onSend() {
      if (selectedId.value === null) return
      const tt = title.value.trim()
      const body = content.value.trim()
      // 后端也会挡（400 title and content required），这里先挡是为了把
      // 「哪一项没填」说清楚，而不是等一个笼统的 400。
      if (!tt || !body) {
        ElMessage.warning(t("manage.messenger.need_title_content"))
        return
      }
      sending.value = true
      const resp = await adminCreateMessage({ userId: selectedId.value, title: tt, content: body })
      sending.value = false
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      ElMessage.success(t("manage.messenger.sent", { name: selectedUser.value?.username ?? "" }))
      // 只清正文、保留标题：管理员常见操作是同一个主题连发几条
      content.value = ""
      await loadMessages()
    }

    async function onDelete(msg: Message) {
      try {
        await ElMessageBox.confirm(
          t("manage.messenger.confirm_delete", { title: msg.title }),
          t("manage.messenger.delete"),
          { type: "warning" },
        )
      } catch {
        return
      }
      const resp = await adminDeleteMessage(msg.id)
      if (resp.code !== 200) {
        ElMessage.error(resp.message)
        return
      }
      await loadMessages()
    }

    onMounted(() => void loadUsers())

    return () => (
      <div class="ol-manage-page">
        <ElCard shadow="never">
          <div class="ol-manage-toolbar">
            <h2 class="ol-manage-h2">{t("manage.messenger.title")}</h2>
            <div class="ol-manage-actions">
              <ElButton size="small" loading={usersLoading.value} onClick={() => void loadUsers()}>
                <ElIcon>
                  <Refresh />
                </ElIcon>
                {t("global.refresh")}
              </ElButton>
            </div>
          </div>

          <ElAlert
            type="info"
            showIcon={true}
            closable={false}
            class="ol-manage-tip"
            title={t("manage.messenger.hint")}
          />

          {users.value.length === 0 && !usersLoading.value ? (
            <ElEmpty description={t("manage.messenger.no_users")} />
          ) : (
            <div class="ol-msg-layout">
              {/* ---- 左：用户选择器 ---- */}
              <aside class="ol-msg-users">
                <ElInput
                  modelValue={keyword.value}
                  placeholder={t("manage.messenger.search_user")}
                  clearable={true}
                  onUpdate:modelValue={(v: string) => (keyword.value = v)}
                  v-slots={{
                    prefix: () => (
                      <ElIcon>
                        <Search />
                      </ElIcon>
                    ),
                  }}
                />
                <div class="ol-msg-user-list">
                  {filteredUsers.value.map((u) => (
                    <button
                      key={u.id}
                      type="button"
                      class={["ol-msg-user", u.id === selectedId.value ? "is-active" : ""]}
                      onClick={() => void selectUser(u.id)}
                    >
                      <span class="ol-msg-user-name">{u.username}</span>
                      <span class="ol-msg-user-meta">
                        {u.is_built_in && (
                          <ElTag size="small" type="warning">
                            {t("manage.messenger.builtin")}
                          </ElTag>
                        )}
                        {u.disabled && (
                          <ElTag size="small" type="info">
                            {t("manage.messenger.disabled")}
                          </ElTag>
                        )}
                        #{u.id}
                      </span>
                    </button>
                  ))}
                  {filteredUsers.value.length === 0 && (
                    <span class="ol-field-help">{t("manage.messenger.no_match")}</span>
                  )}
                </div>
              </aside>

              {/* ---- 右：写信 + 历史 ---- */}
              <section class="ol-msg-main">
                <div class="ol-msg-recipient">
                  <span class="ol-field-help">{t("manage.messenger.recipient")}</span>
                  <strong>{selectedUser.value?.username ?? "-"}</strong>
                  <span class="ol-field-help">
                    {t("manage.messenger.total", { count: messages.value.length })}
                    {unreadCount.value > 0 ? ` · ${t("manage.messenger.unread", { count: unreadCount.value })}` : ""}
                  </span>
                </div>

                <div class="ol-msg-compose">
                  <ElInput
                    modelValue={title.value}
                    placeholder={t("manage.messenger.title_placeholder")}
                    maxlength={120}
                    showWordLimit={true}
                    onUpdate:modelValue={(v: string) => (title.value = v)}
                  />
                  <ElInput
                    type="textarea"
                    modelValue={content.value}
                    placeholder={t("manage.messenger.content_placeholder")}
                    rows={4}
                    maxlength={2000}
                    showWordLimit={true}
                    onUpdate:modelValue={(v: string) => (content.value = v)}
                  />
                  <div class="ol-msg-compose-actions">
                    <ElButton
                      type="primary"
                      loading={sending.value}
                      disabled={selectedId.value === null}
                      onClick={() => void onSend()}
                    >
                      <ElIcon style={{ marginRight: "4px" }}>
                        <Promotion />
                      </ElIcon>
                      {t("manage.messenger.send")}
                    </ElButton>
                  </div>
                </div>

                <div class="ol-msg-list" v-loading={messagesLoading.value}>
                  {messages.value.length === 0 && !messagesLoading.value && (
                    <span class="ol-field-help">{t("manage.messenger.empty")}</span>
                  )}
                  {messages.value.map((m) => (
                    <article key={m.id} class={["ol-msg-item", m.read ? "is-read" : "is-unread"]}>
                      <header class="ol-msg-item-head">
                        {!m.read && <span class="ol-msg-dot" title={t("manage.messenger.unread_one")} />}
                        <span class="ol-msg-item-title">{m.title}</span>
                        <span class="ol-field-help">{formatTime(m.createdAt)}</span>
                        <span title={t("manage.messenger.delete")}>
                          <ElButton size="small" text={true} type="danger" onClick={() => void onDelete(m)}>
                            <ElIcon>
                              <Delete />
                            </ElIcon>
                          </ElButton>
                        </span>
                      </header>
                      <p class="ol-msg-item-body">{m.content}</p>
                    </article>
                  ))}
                </div>
              </section>
            </div>
          )}
        </ElCard>
      </div>
    )
  },
})
