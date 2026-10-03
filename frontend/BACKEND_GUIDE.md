# guAId: как фронту работать с бэкендом

Бэкенд ничего не хранит. Маршрут целиком живёт на фронте, в `localStorage`.
В каждом запросе фронт отправляет текущую точку пользователя как `start`, а бэкенд возвращает до 5 мест рядом с ней.

## Как это выглядит для пользователя

1. Пользователь вводит в поиск, откуда начинает, и выбирает место из подсказок Google.
2. Это место становится первой точкой маршрута.
3. Пользователь пишет в чат, чего хочет, например «хочу кофе», или ничего не пишет. Фронт вызывает `POST /api/route`.
4. Бэкенд возвращает текст и до 5 карточек.
5. Пользователь кликает карточку. Она добавляется в маршрут и становится новым стартом. Фронт сразу запрашивает следующие 5 мест.
6. Шаги 3–5 повторяются. Кнопка «начать заново» очищает маршрут.

## Эндпоинт

Локально: `http://localhost:5104/api/route`. На Azure новый контракт появится после деплоя бэкенда.

### Запрос

```json
{
  "prompt": "хочу кофе",
  "start": {
    "name": "Wawel Castle",
    "google_place_id": "ChIJ9Rk2BW1bFkcRmKV_1sTfuaw",
    "lat": 50.0541,
    "lng": 19.9354
  },
  "visited": ["ChIJ9Rk2BW1bFkcRmKV_1sTfuaw", "ChIJFctE3xFbFkcR681ABM8ayCQ"]
}
```

- `start` обязателен, `lat` и `lng` в нём обязательны.
- `prompt` можно не передавать или передать пустым. Тогда бэкенд предложит интересные места рядом.
- `visited` — `google_place_id` всех точек маршрута, которые пользователь уже выбрал. Бэкенд не вернёт их в `locations`. Поле можно не передавать.

### Ответ

```json
{
  "text": "Вот несколько уютных кафе рядом с вами.",
  "locations": [
    {
      "name": "Camelot Cafe",
      "google_place_id": "ChIJFctE3xFbFkcR681ABM8ayCQ",
      "lat": 50.063,
      "lng": 19.939,
      "description": "Уютное кафе с волшебной атмосферой."
    }
  ]
}
```

- `text` — сообщение для чата. Показывать как есть.
- `locations` — от 0 до 5 мест. Все они реальные, нашлись в Google Maps, без текущего старта, без мест из `visited` и без закрытых. Обычный запрос ищется в радиусе 3 км. Если человек назвал конкретное место, оно может быть до 25 км от старта.
- Если подходящего рядом нет, `locations` пустой, а `text` объясняет это и предлагает, что спросить ещё. Пустой список — нормальный ответ, не ошибка.
- `name`, `google_place_id`, `lat`, `lng` приходят от Google. `description` пишет модель.

Как бэкенд находит места: модель превращает `prompt` в 1–3 поисковые фразы для Google, например «хочу что-то романтичное» превращается в `romantic restaurant`, `wine bar`, `viewpoint`. Для каждой фразы она отмечает, это имя одного места или тип места. Тип ищется в 3 км от старта. Имя конкретного места, даже с опечаткой, ищется до 25 км. Модель выбирает из найденного до 5 мест и пишет `text` и `description`. Поэтому `prompt` может быть любым: на любом языке, размытым, с опечатками.
- `text` и `description` приходят на языке запроса. Если `prompt` пустой, ответ на английском.

### Ошибки

Тело ошибки — стандартный ProblemDetails, человеческий текст лежит в `detail`.

- `400` — нет `start` или в нём нет `lat`/`lng`.
- `500` — на сервере не заданы ключи OpenAI или Google.
- `502` — OpenAI или Google ответили ошибкой. Можно показать «попробуйте ещё раз».

## Типы

Карточка и старт имеют одинаковые поля, поэтому выбранную карточку можно класть в маршрут как есть.

```ts
type Place = {
  name: string;
  google_place_id: string;
  lat: number;
  lng: number;
  description?: string;
};

type RouteResponse = {
  text: string;
  locations: Place[];
};
```

## Шаг 1. Маршрут в localStorage

```tsx
const ROUTE_KEY = 'guaid.route';

const [route, setRoute] = useState<Place[]>(() =>
  JSON.parse(localStorage.getItem(ROUTE_KEY) ?? '[]'));

useEffect(() => {
  localStorage.setItem(ROUTE_KEY, JSON.stringify(route));
}, [route]);

const start = route.at(-1) ?? null;
```

После перезагрузки страницы маршрут восстановится, и пользователь продолжит с последней точки.

## Шаг 2. Поиск стартовой точки

Используем `PlaceAutocompleteElement` из библиотеки `places`. Компонент должен быть внутри `<APIProvider>`.

```tsx
import { useMapsLibrary } from '@vis.gl/react-google-maps';

function StartSearch({ onSelect }: { onSelect: (place: Place) => void }) {
  const places = useMapsLibrary('places');
  const container = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!places || !container.current) return;

    const input = new places.PlaceAutocompleteElement({});
    container.current.appendChild(input);

    input.addEventListener('gmp-select', async (event: any) => {
      const place = event.placePrediction.toPlace();
      await place.fetchFields({ fields: ['id', 'displayName', 'location'] });
      onSelect({
        name: place.displayName ?? '',
        google_place_id: place.id,
        lat: place.location.lat(),
        lng: place.location.lng(),
      });
    });

    return () => input.remove();
  }, [places]);

  return <div ref={container} />;
}
```

Использование: пока маршрут пустой, показываем поиск вместо чата.

```tsx
{route.length === 0 && <StartSearch onSelect={place => setRoute([place])} />}
```

## Шаг 3. Запрос к бэкенду

Маршрут передаём явно, потому что после `setRoute` новое значение появится только на следующем рендере.
Последняя точка маршрута уходит как `start`, а `google_place_id` всех точек — как `visited`.

```tsx
const API_URL = 'http://localhost:5104';

async function askRoute(prompt: string, path: Place[]) {
  const from = path[path.length - 1];
  const response = await fetch(`${API_URL}/api/route`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      prompt,
      start: {
        name: from.name,
        google_place_id: from.google_place_id,
        lat: from.lat,
        lng: from.lng,
      },
      visited: path.map(place => place.google_place_id),
    }),
  });

  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new Error(problem?.detail ?? 'Request failed');
  }

  return (await response.json()) as RouteResponse;
}
```

Отправка из чата:

```tsx
const data = await askRoute(userText, route);
// data.text -> сообщение в чат, data.locations -> карточки
```

## Шаг 4. Клик по карточке

Карточка добавляется в маршрут, и сразу запрашиваются места от неё. Пустой `prompt` означает «предложи что-то интересное рядом».

```tsx
async function pickPlace(card: Place) {
  const next = [...route, card];
  setRoute(next);
  const data = await askRoute('', next);
  // показать data.text и data.locations
}
```

```tsx
<div onClick={() => pickPlace(loc)}> ... </div>
```

## Шаг 5. Карта

- Линию маршрута строим по `route`, то есть по точкам, которые пользователь уже выбрал.
- 5 предложенных мест показываем только маркерами, без линии: это варианты выбора, а не маршрут.

## Шаг 6. Начать заново

```tsx
<button onClick={() => setRoute([])}>Начать заново</button>
```

## Что поправить в текущем App.tsx

- Убрать `data.city` и сообщение, собранное на фронте. Показывать `data.text`.
- Брать `google_place_id` вместо `placeId`.
- Убрать заглушки `rating` и `estimatedCost`: бэкенд их не возвращает.
- Не блокировать отправку пустого сообщения, если старт уже выбран: пустой `prompt` — нормальный запрос.
- Поменять URL API на `http://localhost:5104` для локальной проверки.

## Настройка Google Cloud

Для ключа `VITE_GOOGLE_MAPS_API_KEY`:

- включить Places API (New), без него поиск старта не работает;
- в ограничениях ключа разрешить домен фронта, локально `http://localhost:5173`.