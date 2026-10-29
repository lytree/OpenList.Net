import { defineStore } from "pinia"
import { computed, ref } from "vue"
import { fetchInitStatus } from "~/api/init"

/**
 * 系统初始化状态 store。
 *
 * 只回答一个问题：「这台 OpenList 有没有管理员账号」。没有就必须先把人送进安装向导
 * （`/@init`），否则登录页永远只会回「用户名或密码错误」—— 后端在既非开发模式、
 * 又没配 `AdminPassword` 时**不建**任何管理员，凭据根本不存在。
 *
 * 三条容错约定：
 * 1. **默认视为已初始化**：探测失败（后端没起来、接口 404、网络断）不该把用户锁在
 *    向导页上反复提交。宁可让登录页自己报错，也不能让人连站点都打不开。
 * 2. 只探测一次，结果缓存在内存里；向导建号成功后由页面显式 `markInitialized()`。
 * 3. 状态不做持久化：管理员随时可能被删（CLI `user delete`），下次冷启动应该重新问后端。
 */
export const useInitStore = defineStore("init", () => {
  const initialized = ref(true)
  const checked = ref(false)
  const checking = ref(false)

  const needsSetup = computed(() => checked.value && !initialized.value)

  async function check(force = false) {
    if (checking.value) return initialized.value
    if (checked.value && !force) return initialized.value

    checking.value = true
    try {
      const resp = await fetchInitStatus()
      if (resp.code === 200 && resp.data) {
        initialized.value = resp.data.initialized !== false
      }
      // 非 200 时保持上一次的结论（首次即「已初始化」），见文件头第 1 条。
      checked.value = true
    } catch {
      // 探测本身抛异常（离线、被网关劫持返回 HTML…）：吞掉。
      // 让守卫继续走正常流程，而不是让整个导航因为一次探测失败直接中断。
      checked.value = true
    } finally {
      checking.value = false
    }
    return initialized.value
  }

  /** 向导建号成功后调用，避免守卫把人又踢回向导页。 */
  function markInitialized() {
    initialized.value = true
    checked.value = true
  }

  return { initialized, checked, checking, needsSetup, check, markInitialized }
})
