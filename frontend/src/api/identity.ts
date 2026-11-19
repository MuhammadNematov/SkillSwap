// Id текущего пользователя нужен адаптерам ответов бэкенда (кто преподаёт, чьё сообщение).
// Ставится синхронно при входе и выходе в AuthContext.
let myId = ''
export const setMyId = (id: string) => {
  myId = id
}
export const getMyId = () => myId
