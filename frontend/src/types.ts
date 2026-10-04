export type Place = {
  name: string;
  google_place_id: string;
  lat: number;
  lng: number;
  description?: string;
  rating?: number | null;
  user_rating_count?: number | null;
  rating_summary?: string;
  review_summary?: string;
  price_level?: string;
  price?: string;
  open_now?: boolean | null;
  opening_hours?: string[];
  photo_url?: string;
  photo_author?: string;
  photo_author_uri?: string;
  photos?: {
    url: string;
    author: string;
    author_uri: string;
  }[];
};

export type PlanQuery = { text: string; named: boolean };
export type PlanStep = { label: string; queries: PlanQuery[] };
export type RouteMode = 'spot' | 'plan';

export type RouteResponse = {
  text: string;
  locations: Place[];
  plan: PlanStep[];
};

export type Message = {
  id: string;
  sender: 'user' | 'ai';
  text: string;
  pendingLocations?: Place[];
  isTyping?: boolean;
};

export type UIState = 'chat' | 'slider';