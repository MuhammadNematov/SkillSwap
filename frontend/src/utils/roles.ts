import type { Role } from '../api/types'

// Куда отправлять пользователя после входа / при нехватке прав
export function homeForRole(role: Role): string {
  switch (role) {
    case 'Moderator':
      return '/moderation'
    case 'Admin':
    case 'Superadmin':
      return '/admin'
    default:
      return '/app'
  }
}
