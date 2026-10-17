export interface SessionUser {
  username: string
  roles: string[]
}

export interface LoginCredentials {
  username: string
  password: string
}

export type ApiErrorKind = 'unauthorized' | 'forbidden' | 'http' | 'network' | 'invalid-response'
