# guAId: как фронту работать с бэкендом

Бэкенд ничего не хранит. Маршрут и очередь плана живут на фронте, в `localStorage`.
В каждом запросе фронт отправляет текущую точку пользователя как `start`, а бэкенд возвращает до 10 мест рядом с ней.

На экране две кнопки. Они меняют поле `mode` в том же `POST /api/route`.

- «На месте» отправляет `"mode": "spot"`. Один запрос — один следующий шаг, как раньше.
- «План» отправляет `"mode": "plan"`. Фраза режется на шаги, но карточки приходят только для текущего шага. Хвост очереди лежит в `plan`.

## Как это выглядит для пользователя

1. Пользователь вводит в поиск, откуда начинает, и выбирает место из подсказок Google.
2. Это место становится первой точкой маршрута.
3. Пользователь выбирает кнопку «На месте» или «План» и пишет в чат, чего хочет. Пустое сообщение тоже можно отправить. Фронт вызывает `POST /api/route`.
4. Бэкенд возвращает текст и до 10 карточек для одной позиции.
5. Пользователь кликает карточку. Она добавляется в маршрут и становится новым стартом.
6. В режиме «На месте» фронт сразу просит следующие места с пустым `prompt`. В режиме «План», если очередь не пустая, фронт сам отправляет следующий шаг и показывает карточки уже для него.
7. Кнопка «начать заново» очищает маршрут и очередь.

## Эндпоинт

Локально: `http://localhost:5104/api/route`. На Azure новый контракт появится после деплоя бэкенда.

### Запрос

```json
{
  "prompt": "хочу кофе",
  "mode": "spot",
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
- `mode` — `spot` или `plan`. Если поля нет или оно пустое, бэкенд считает это `spot`.
- `prompt` можно не передавать или передать пустым. Тогда бэкенд предложит интересные места рядом, а `plan` будет пустым.
- `visited` — `google_place_id` всех точек маршрута, которые пользователь уже выбрал. Бэкенд не вернёт их в `locations`. Поле можно не передавать.
- `step` нужен только продолжению плана, когда человек кликнул карточку и в очереди ещё есть шаги. В `spot` это поле игнорируется.

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
      "description": "Уютное кафе с волшебной атмосферой.",
      "photo_url": "https://lh3.googleusercontent.com/place-photo",
      "photo_author": "Jane Doe",
      "photo_author_uri": "https://maps.google.com/maps/contrib/123"
    }
  ],
  "plan": []
}
```

- `text` — сообщение для чата. Показывать как есть.
- `locations` — от 0 до 10 мест для одной позиции. Все они реальные, нашлись в Google Maps, без текущего старта, без мест из `visited` и без закрытых. Обычный запрос ищется в радиусе 3 км. Если человек назвал конкретное место, оно может быть до 25 км от старта.
- Если подходящих мест меньше 10, но они есть, бэкенд добирает ближайшие из того же поиска. У добранных `description` может быть пустым. Карточку всё равно показываем.
- Если подходящего рядом нет, `locations` пустой, а `text` объясняет это и предлагает, что спросить ещё. Пустой список — нормальный ответ, не ошибка. В этом случае список не добивается.
- `name`, `google_place_id`, `lat`, `lng` приходят от Google. `description` пишет модель.
- `plan` — хвост очереди. В `spot` он всегда `[]`. В `plan` там следующие шаги, без текущего. Поле есть всегда.
- Поля фото описаны ниже, в разделе «Картинки».

Как бэкенд находит места в `spot`: модель превращает `prompt` в 1–3 поисковые фразы для Google, например «хочу что-то романтичное» превращается в `romantic restaurant`, `wine bar`, `viewpoint`. Для каждой фразы она отмечает, это имя одного места или тип места. Тип ищется в 3 км от старта. Имя конкретного места, даже с опечаткой, ищется до 25 км. Модель выбирает из найденного до 10 мест и пишет `text` и `description`. Поэтому `prompt` может быть любым: на любом языке, размытым, с опечатками.
- `text` и `description` приходят на языке запроса. Если `prompt` пустой, ответ на английском.

## Режим plan

Кнопка «План» шлёт `"mode": "plan"` и текст человека, без `step`.

Модель режет фразу на 1–4 шага. Отдельный шаг появляется в двух случаях:

- в тексте есть порядок: «потом», «затем», «сначала», «после», «then», «after»;
- через «и» соединены заведомо разные заведения, например барбер и магазин.

«Кальян и покушать» или «кофе и десерт» остаются одним шагом: это одно заведение. «Музей и кофе» тоже один шаг, потому что непонятно, это два места или одно. Размытое желание без порядка, например «что-то романтичное», тоже один шаг, у него может быть до 3 поисковых фраз.

Ищется только первый шаг. В `locations` — до 10 карточек для него. Остальные шаги приходят в `plan` и на карте не рисуются.

```json
{
  "prompt": "поесть, потом барбер, потом в магаз",
  "mode": "plan",
  "start": {
    "name": "Wawel Castle",
    "google_place_id": "ChIJ9Rk2BW1bFkcRmKV_1sTfuaw",
    "lat": 50.0541,
    "lng": 19.9354
  },
  "visited": ["ChIJ9Rk2BW1bFkcRmKV_1sTfuaw"]
}
```

```json
{
  "text": "Сначала где поесть. Когда выберешь, покажу барбера, потом магазин.",
  "locations": [],
  "plan": [
    {
      "label": "барбершоп",
      "queries": [{ "text": "barber shop", "named": false }]
    },
    {
      "label": "магазин",
      "queries": [{ "text": "supermarket", "named": false }]
    }
  ]
}
```

`label` — короткая подпись на языке человека. `queries` — фразы для Google. `named: true` значит имя конкретного места, его можно искать до 25 км. `named: false` — тип места, радиус 3 км.

Фронт сохраняет `plan` как очередь. Новое сообщение из чата эту очередь заменяет. В `spot` очередь очищается.

Клик по карточке, пока очередь не пустая, отправляет первый элемент очереди как `step`. Фронт заранее убирает его из очереди. В ответе на такой запрос `plan` пустой, свою очередь им заменять не нужно.

```json
{
  "prompt": "барбершоп",
  "mode": "plan",
  "step": {
    "label": "барбершоп",
    "queries": [{ "text": "barber shop", "named": false }]
  },
  "start": {
    "name": "Camelot Cafe",
    "google_place_id": "ChIJFctE3xFbFkcR681ABM8ayCQ",
    "lat": 50.063,
    "lng": 19.939
  },
  "visited": ["ChIJ9Rk2BW1bFkcRmKV_1sTfuaw", "ChIJFctE3xFbFkcR681ABM8ayCQ"]
}
```

`prompt` здесь — `label` шага, чтобы ответ остался на языке человека. Ищутся `step.queries`, от нового `start`. Когда очередь кончилась, клик снова шлёт пустой `prompt` и `"mode": "plan"` без `step`: дальше обычные места рядом.

### Ошибки

Тело ошибки — стандартный ProblemDetails, человеческий текст лежит в `detail`.

- `400` — нет `start` или в нём нет `lat`/`lng`. Либо `mode` не `spot` и не `plan`. Либо в `plan` прислан `step` без поисковой фразы.
- `500` — на сервере не заданы ключи OpenAI или Google.
- `502` — OpenAI или Google ответили ошибкой. Можно показать «попробуйте ещё раз».

## Картинки

Бэкенд не присылает файл. У каждой локации в ответе три строки:

- `photo_url` — адрес картинки на `lh3.googleusercontent.com`. Его ставят в `<img src>`. Браузер скачивает фото сам, напрямую у Google.
- `photo_author` — имя автора.
- `photo_author_uri` — страница автора.

Если фото нет, все три поля приходят пустыми. Карточку всё равно показываем, просто без картинки. То же самое, если `<img>` не загрузился: прячем картинку, место остаётся.

Рядом с фото нужно показать автора. Это требование Google. Если `photo_author` пустой, подпись не нужна. Если `photo_author_uri` не пустой, имя должно быть ссылкой на него.

`photo_url` живёт недолго. В `localStorage` его не кладём. После перезагрузки старая ссылка может не открыться. Новый запрос `POST /api/route` приносит свежие ссылки.

В `start` эти поля слать не нужно. Для старта по-прежнему хватает `name`, `google_place_id`, `lat`, `lng`.

## Типы

Карточка и старт имеют одинаковые поля, поэтому выбранную карточку можно класть в маршрут как есть.

```ts
type Place = {
  name: string;
  google_place_id: string;
  lat: number;
  lng: number;
  description?: string;
  photo_url?: string;
  photo_author?: string;
  photo_author_uri?: string;
};

type PlanQuery = {
  text: string;
  named: boolean;
};

type PlanStep = {
  label: string;
  queries: PlanQuery[];
};

type RouteMode = 'spot' | 'plan';

type RouteResponse = {
  text: string;
  locations: Place[];
  plan: PlanStep[];
};
```

## Шаг 1. Маршрут в localStorage

```tsx
const ROUTE_KEY = 'guaid.route';
const PLAN_KEY = 'guaid.plan';
const MODE_KEY = 'guaid.mode';

const [route, setRoute] = useState<Place[]>(() =>
  JSON.parse(localStorage.getItem(ROUTE_KEY) ?? '[]'));
const [plan, setPlan] = useState<PlanStep[]>(() =>
  JSON.parse(localStorage.getItem(PLAN_KEY) ?? '[]'));
const [mode, setMode] = useState<RouteMode>(() =>
  localStorage.getItem(MODE_KEY) === 'plan' ? 'plan' : 'spot');

useEffect(() => {
  localStorage.setItem(ROUTE_KEY, JSON.stringify(route));
}, [route]);

useEffect(() => {
  localStorage.setItem(PLAN_KEY, JSON.stringify(plan));
}, [plan]);

useEffect(() => {
  localStorage.setItem(MODE_KEY, mode);
}, [mode]);

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

async function askRoute(
  prompt: string,
  path: Place[],
  mode: RouteMode,
  step?: PlanStep,
) {
  const from = path[path.length - 1];
  const response = await fetch(`${API_URL}/api/route`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      prompt,
      mode,
      ...(step ? { step } : {}),
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
const data = await askRoute(userText, route, mode);
setPlan(mode === 'plan' ? data.plan : []);
// data.text -> сообщение в чат, data.locations -> карточки
```

Кнопки только меняют `mode`. Очередь меняет следующее сообщение, не сам клик по кнопке.

## Шаг 4. Клик по карточке

Карточка добавляется в маршрут. Маршрут и очередь передаём аргументами: после `setRoute` новое значение появится только на следующем рендере.

В `spot`, и в `plan` с пустой очередью, уходит пустой `prompt`. В `plan` с непустой очередью уходит следующий шаг, а из очереди он уже снят. `data.plan` в этом ответе пустой, им очередь не затираем.

```tsx
async function pickPlace(card: Place, path: Place[], queue: PlanStep[]) {
  const next = [...path, card];
  setRoute(next);

  if (mode === 'plan' && queue.length > 0) {
    const [step, ...rest] = queue;
    setPlan(rest);
    const data = await askRoute(step.label, next, 'plan', step);
    // показать data.text и data.locations
    return;
  }

  const data = await askRoute('', next, mode);
  // показать data.text и data.locations
}
```

```tsx
<div onClick={() => pickPlace(loc)}> ... </div>
```

## Шаг 5. Карта

- Линию маршрута строим по `route`, то есть по точкам, которые пользователь уже выбрал.
- До 10 предложенных мест показываем только маркерами, без линии: это варианты выбора, а не маршрут.
- Шаги из `plan` на карту не выводим. Их ещё не искали.

## Шаг 6. Начать заново

```tsx
<button onClick={() => { setRoute([]); setPlan([]); }}>Начать заново</button>
```

## Что поправить в текущем App.tsx

- Убрать `data.city` и сообщение, собранное на фронте. Показывать `data.text`.
- Брать `google_place_id` вместо `placeId`.
- Убрать заглушки `rating` и `estimatedCost`: бэкенд их не возвращает.
- Не блокировать отправку пустого сообщения, если старт уже выбран: пустой `prompt` — нормальный запрос.
- С каждым запросом слать `mode`: `spot` или `plan`, по нажатой кнопке.
- В `plan` хранить `data.plan` и при клике на карточку отправлять следующий `step`, как в шаге 4.
- Поменять URL API на `http://localhost:5104` для локальной проверки.

## Настройка Google Cloud

Для ключа `VITE_GOOGLE_MAPS_API_KEY`:

- включить Places API (New), без него поиск старта не работает;
- в ограничениях ключа разрешить домен фронта, локально `http://localhost:5173`.