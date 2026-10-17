export interface SessionUser {
  username: string
  role: 'owner' | 'reviewer'
}

export interface LoginCredentials {
  username: string
  password: string
}

export type ApiErrorKind = 'unauthorized' | 'forbidden' | 'http' | 'network' | 'invalid-response'
